namespace SomeDataProvider.DataStorage.TInvest.Test;

using System.Net;
using Grpc.Core;
using NUnit.Framework;
using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class ProbeFillTests
{
	[Test]
	public void ProbeArchivePathsCannotReplaceOrDeleteAUserDay()
	{
		using var fixture = new TickFixture();
		var day = new DateOnly(2026, 10, 28);
		var uid = fixture.Symbol.Instrument.Short.Uid;
		var userPath = fixture.Store.ArchiveCache.PathFor(uid, day);
		Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
		var original = TickFixture.Gzip(TickFixture.Csv());
		File.WriteAllBytes(userPath, original);
		using var scopes = new ProbeScopeRegistry(fixture.Clock);
		using (ProbeContext.Enter(scopes.Open("synthetic-probe", 180, 2, 0)))
		{
			var probePath = fixture.Store.ArchiveCache.PathFor(uid, day);
			Assert.That(probePath, Is.Not.EqualTo(userPath));
			Directory.CreateDirectory(Path.GetDirectoryName(probePath)!);
			File.WriteAllBytes(probePath, TickFixture.Zip(TickFixture.Csv()));
			fixture.Store.ArchiveCache.Delete(probePath);
		}
		Assert.That(File.ReadAllBytes(userPath), Is.EqualTo(original));
	}

	[Test]
	public async Task QuotaDeferredBackgroundFillsStillSpendTheOriginalScope()
	{
		var clock = new ManualTimeProvider();
		clock.Advance(TickFixture.Now - DateTimeOffset.UnixEpoch);
		var limiter = new TInvestRateLimiter(clock);
		limiter.Correct(TInvestLimitGroup.Archives, null, 0, TimeSpan.FromSeconds(60), true);
		using var handler = new CountingHandler();
		using var gateway = new SdkTInvestGateway(new NoUnaryInvoker(), new TInvestOptions(), limiter, clock);
		gateway.UseArchiveClient(() => new TradeArchiveClient(handler, new Uri("https://synthetic.invalid/"), limiter, clock));
		var folder = Path.Combine(Path.GetTempPath(), "sdp-probe-fill-" + Guid.NewGuid().ToString("N"));
		using var store = new TInvestStore(gateway, new TInvestOptions { ArchiveCacheFolder = folder }, clock);
		using var scopes = new ProbeScopeRegistry(clock);
		var scope = scopes.Open("synthetic-probe", 180, 1, 0);
		using (ProbeContext.Enter(scope))
		{
			Assert.ThrowsAsync<TInvestRequestException>(() => store.CreateTickReaderAsync(new TInvestSymbol(ScriptedTInvestGateway.Invent()),
				TickFixture.Utc(10, 27), TickFixture.Utc(10, 29), clock.GetUtcNow().AddSeconds(20)));
		}
		var fills = store.BackgroundFills;
		clock.Advance(TimeSpan.FromSeconds(60));
		try { await fills.WaitAsync(TimeSpan.FromSeconds(5)); }
		catch (ProbeRejectedException) { }
		catch (OperationCanceledException) { }
		Assert.That(handler.Calls, Is.EqualTo(1));
		Assert.That(handler.Owner, Is.SameAs(scope));
		Assert.That(scope.Remaining.TInvest, Is.Zero);
		try { Directory.Delete(folder, true); } catch (DirectoryNotFoundException) { }
	}

	[Test]
	public async Task AUserDoesNotJoinAProbeOwnedArchiveFill()
	{
		var clock = new ManualTimeProvider();
		clock.Advance(TickFixture.Now - DateTimeOffset.UnixEpoch);
		var limiter = new TInvestRateLimiter(clock);
		using var handler = new FillHandler();
		using var gateway = new SdkTInvestGateway(new NoUnaryInvoker(), new TInvestOptions(), limiter, clock);
		gateway.UseArchiveClient(() => new TradeArchiveClient(handler, new Uri("https://synthetic.invalid/"), limiter, clock));
		var folder = Path.Combine(Path.GetTempPath(), "sdp-probe-fill-" + Guid.NewGuid().ToString("N"));
		using var store = new TInvestStore(gateway, new TInvestOptions { ArchiveCacheFolder = folder }, clock);
		using var scopes = new ProbeScopeRegistry(clock);
		var scope = scopes.Open("synthetic-probe", 180, 10, 0);
		Task<TInvestTickReader> probe;
		using (ProbeContext.Enter(scope)) probe = Read();
		await handler.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var user = Read();
		try
		{
			await handler.UserEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.ThrowsAsync<TInvestRequestException>(async () => await user);
		}
		finally
		{
			handler.Release.TrySetResult();
			Assert.CatchAsync(async () => await probe.WaitAsync(TimeSpan.FromSeconds(5)));
			try { Directory.Delete(folder, true); } catch (DirectoryNotFoundException) { }
		}
		Assert.That(scope.Remaining.TInvest, Is.EqualTo(9));

		Task<TInvestTickReader> Read() => store.CreateTickReaderAsync(new TInvestSymbol(ScriptedTInvestGateway.Invent()),
			TickFixture.Utc(10, 28), TickFixture.Utc(10, 29), clock.GetUtcNow().AddSeconds(20));
	}

	sealed class CountingHandler : HttpMessageHandler
	{
		int _calls;
		public int Calls => _calls;
		public ProbeScope? Owner { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _calls);
			Owner = ProbeContext.Current;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
		}
	}

	sealed class FillHandler : HttpMessageHandler
	{
		public TaskCompletionSource ProbeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource UserEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (ProbeContext.IsProbe)
			{
				ProbeEntered.TrySetResult();
				await Release.Task.WaitAsync(cancellationToken);
			}
			else
			{
				UserEntered.TrySetResult();
			}
			return new(HttpStatusCode.NotFound);
		}
	}

	sealed class NoUnaryInvoker : CallInvoker
	{
		public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
		public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
	}
}
