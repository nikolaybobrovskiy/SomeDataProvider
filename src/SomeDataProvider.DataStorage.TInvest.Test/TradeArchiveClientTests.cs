namespace SomeDataProvider.DataStorage.TInvest.Test;

using System.Net;
using System.Net.Http.Headers;
using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;
using static SomeDataProvider.DataStorage.TInvest.Test.TickFixture;

[TestFixture]
public class TradeArchiveClientTests
{
	static readonly DateOnly Day = new(2026, 10, 28);

	[Test]
	public async Task ProbeArchiveRetryCannotBypassItsOneAttemptCeiling()
	{
		var calls = 0;
		using var client = new Scope((_, _) =>
		{
			calls++;
			var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
			response.Headers.Add("x-ratelimit-reset", "1");
			return response;
		});
		using var scopes = new SomeDataProvider.DataStorage.Definitions.ProbeScopeRegistry(client.Clock);
		using var probe = SomeDataProvider.DataStorage.Definitions.ProbeContext.Enter(scopes.Open("synthetic-probe", 180, 1, 0));
		var task = client.Download();
		client.Clock.Advance(TimeSpan.FromSeconds(1));
		Assert.ThrowsAsync<SomeDataProvider.DataStorage.Definitions.ProbeRejectedException>(async () => await task);
		Assert.That(calls, Is.EqualTo(1));
		await Task.CompletedTask;
	}

	[Test]
	public async Task DownloadsWithoutAnyCredentialAndKeepsTheBytesAsReceived()
	{
		using var scope = new Scope((request, _) =>
		{
			Assert.That(request.Headers.Authorization, Is.Null, "The archive host needs no token.");
			Assert.That(request.RequestUri!.AbsolutePath, Is.EqualTo("/history-trades/2026-10-28"));
			Assert.That(request.RequestUri.Query, Is.EqualTo("?instrumentId=SYNTH_TESTCC"));
			return Ok(Zip(Csv()));
		});
		Assert.That(await scope.Download(), Is.True);
		Assert.That(File.ReadAllBytes(scope.Path), Is.EqualTo(Zip(Csv())));
		Assert.That(Directory.GetFiles(Directory.GetParent(scope.Path)!.FullName), Has.Length.EqualTo(1), "No partial file stays.");
	}

	[TestCase(1)]
	[TestCase(3)]
	public async Task AValidArchiveDeliveredInTinyReadsIsAccepted(int chunk)
	{
		var bytes = Zip(Csv());
		using var scope = new Scope((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TrickleStream(bytes, chunk)) });
		Assert.That(await scope.Download(), Is.True);
		Assert.That(File.ReadAllBytes(scope.Path), Is.EqualTo(bytes));
	}

	[Test]
	public void AFileShorterThanTheMagicIsRejected()
	{
		using var scope = new Scope((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TrickleStream(new byte[] { 0x50, 0x4B }, 1)) });
		Assert.That(Assert.ThrowsAsync<TInvestRequestException>(() => scope.Download())!.Message, Does.Contain("unexpected format"));
		Assert.That(File.Exists(scope.Path), Is.False);
	}

	[Test]
	public void ArchivesAreFetchedFromTheProductionHostAlsoInSandbox()
	{
		Assert.That(SdkTInvestGateway.ArchiveHost, Is.EqualTo("https://invest-public-api.tbank.ru/"));
	}

	[Test]
	public void CacheDatesSurviveARussianProcessCulture()
	{
		var old = System.Globalization.CultureInfo.CurrentCulture;
		var folder = Path.Combine(Path.GetTempPath(), "sdp-culture-" + Guid.NewGuid().ToString("N"));
		try
		{
			System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
			var cache = new TradeArchiveCache(folder, 1024L * 1024, new ManualTimeProvider());
			var path = cache.PathFor("SYNTH_TESTCC", Day);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, new byte[] { 1 });
			Assert.That(cache.NewestDay(), Is.EqualTo(Day));
		}
		finally
		{
			System.Globalization.CultureInfo.CurrentCulture = old;
			if (Directory.Exists(folder)) Directory.Delete(folder, true);
		}
	}

	[Test]
	public void CountStopsAtTheCapAndHonoursCancellation()
	{
		var path = Path.Combine(Path.GetTempPath(), "sdp-count-" + Guid.NewGuid().ToString("N") + ".gz");
		try
		{
			var rows = Enumerable.Range(0, 10).Select(i => $"2026-10-28T10:00:0{i}Z,SYNTH_TESTCC,TRADE_DIRECTION_BUY,10.5,1,EXCHANGE,uid").ToArray();
			File.WriteAllBytes(path, Gzip(Csv(rows)));
			Assert.That(TradeArchiveReader.Count(path, Day, false, null, null, 3), Is.EqualTo(4), "Counting stops one past the cap.");
			using var cancelled = new CancellationTokenSource();
			cancelled.Cancel();
			Assert.Throws<OperationCanceledException>(() => TradeArchiveReader.Count(path, Day, false, null, null, int.MaxValue, cancelled.Token));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Test]
	public async Task NotFoundMeansNoArchive()
	{
		using var scope = new Scope((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
		Assert.That(await scope.Download(), Is.False);
		Assert.That(File.Exists(scope.Path), Is.False);
	}

	[Test]
	public void AnythingButGzipOrZipFailsAndLeavesNothing()
	{
		using var scope = new Scope((_, _) => Ok(System.Text.Encoding.UTF8.GetBytes("<html>sign in</html>")));
		Assert.That(Assert.ThrowsAsync<TInvestRequestException>(() => scope.Download())!.Message, Does.Contain("unexpected format"));
		Assert.That(File.Exists(scope.Path), Is.False);
		Assert.That(Directory.Exists(Directory.GetParent(scope.Path)!.FullName) ? Directory.GetFiles(Directory.GetParent(scope.Path)!.FullName) : Array.Empty<string>(), Is.Empty);
	}

	[Test]
	public void ServerErrorsGiveAGenericFailure()
	{
		using var scope = new Scope((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("secret upstream text") });
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => scope.Download());
		Assert.That(error!.Message, Is.EqualTo("T-Invest archive request failed."));
	}

	[Test]
	public async Task Http429BlocksTheWindowUntilResetAndRetriesOnceWhenTheDeadlineAllows()
	{
		var calls = 0;
		using var scope = new Scope((_, _) =>
		{
			calls++;
			if (calls > 1) return Ok(Gzip(Csv()));
			var response = new HttpResponseMessage((HttpStatusCode)429);
			response.Headers.Add("x-ratelimit-reset", "5");
			return response;
		});
		var task = scope.Download();
		for (var i = 0; i < 5000 && !task.IsCompleted; i++)
		{
			scope.Clock.Advance(TimeSpan.FromSeconds(1));
			await Task.Yield();
		}

		Assert.That(await task.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
		Assert.That(calls, Is.EqualTo(2));
	}

	[TestCase("600", 600)]
	[TestCase(null, 60)]
	public void Http429WhoseResetDoesNotFitTheDeadlineFailsWithRetryInAndNoSecondRequest(string? header, int expected)
	{
		var calls = 0;
		using var scope = new Scope((_, _) =>
		{
			calls++;
			var response = new HttpResponseMessage((HttpStatusCode)429);
			if (header != null) response.Headers.Add("x-ratelimit-reset", header);
			return response;
		});
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => scope.Download());
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		Assert.That(error.Message, Does.Contain($"retry in {expected} s"));
		Assert.That(calls, Is.EqualTo(1));
		Assert.That(scope.Limiter.TryAcquire(TInvestLimitGroup.Archives), Is.GreaterThan(TimeSpan.Zero), "The archive window stays blocked until the reset.");
	}

	static HttpResponseMessage Ok(byte[] bytes)
	{
		var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
		response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
		return response;
	}

	sealed class Scope : IDisposable
	{
		readonly TradeArchiveClient _client;

		public Scope(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
		{
			Clock.Advance(Now - DateTimeOffset.UnixEpoch);
			Limiter = new TInvestRateLimiter(Clock);
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sdp-client-" + Guid.NewGuid().ToString("N"), "archive");
			_client = new TradeArchiveClient(new Handler(respond), new Uri("https://archive.invalid/"), Limiter, Clock);
		}

		public ManualTimeProvider Clock { get; } = new();
		public TInvestRateLimiter Limiter { get; }
		public string Path { get; }

		public Task<bool> Download() => _client.DownloadAsync("SYNTH_TESTCC", Day, Path, null, Clock.GetUtcNow().AddSeconds(20), default);

		public void Dispose()
		{
			_client.Dispose();
			var folder = System.IO.Path.GetDirectoryName(Path)!;
			if (Directory.Exists(folder)) Directory.Delete(folder, true);
		}
	}

	sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request, cancellationToken));
	}

	sealed class TrickleStream(byte[] bytes, int chunk) : MemoryStream(bytes)
	{
		public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));
		public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, chunk)]);
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
	}
}

[TestFixture]
public class TickOptionsTests
{
	static readonly string Folder = Path.Combine(Path.GetTempPath(), "sdp-options");

	[Test]
	public void DefaultsAreInsideTheirRanges()
	{
		var options = new TInvestOptions();
		options.Validate();
		Assert.That((options.MaxTickDaysPerRequest, options.MaxTicksPerRequest, options.ArchiveCacheMaxBytes), Is.EqualTo((31, 5_000_000, 20L * 1024 * 1024 * 1024)));
		Assert.That(options.EffectiveArchiveCacheFolder, Does.EndWith(Path.Combine("SomeDataProvider", "tinvest-archives")));
		Assert.That(Path.IsPathFullyQualified(options.EffectiveArchiveCacheFolder), Is.True);
	}

	[TestCase(1, true)]
	[TestCase(31, true)]
	[TestCase(0, false)]
	[TestCase(32, false)]
	[TestCase(-1, false)]
	public void MaxTickDaysPerRequestRange(int value, bool valid) => Check(new TInvestOptions { MaxTickDaysPerRequest = value }, valid, "MaxTickDaysPerRequest must be 1-31");

	[TestCase(1_000, true)]
	[TestCase(5_000_000, true)]
	[TestCase(999, false)]
	[TestCase(5_000_001, false)]
	[TestCase(0, false)]
	public void MaxTicksPerRequestRange(int value, bool valid) => Check(new TInvestOptions { MaxTicksPerRequest = value }, valid, "MaxTicksPerRequest must be 1000-5000000");

	[TestCase(100L * 1024 * 1024, true)]
	[TestCase(1024L * 1024 * 1024 * 1024, true)]
	[TestCase(100L * 1024 * 1024 - 1, false)]
	[TestCase(1024L * 1024 * 1024 * 1024 + 1, false)]
	[TestCase(0L, false)]
	public void ArchiveCacheMaxBytesRange(long value, bool valid) => Check(new TInvestOptions { ArchiveCacheMaxBytes = value }, valid, "ArchiveCacheMaxBytes must be 104857600");

	[Test]
	public void ArchiveCacheFolderMustBeAFullPathOrOmitted()
	{
		Check(new TInvestOptions { ArchiveCacheFolder = null }, true, string.Empty);
		Check(new TInvestOptions { ArchiveCacheFolder = Folder }, true, string.Empty);
		Check(new TInvestOptions { ArchiveCacheFolder = new string('a', 1) + ":" }, false, "ArchiveCacheFolder must be a full path");
		Check(new TInvestOptions { ArchiveCacheFolder = string.Empty }, false, "ArchiveCacheFolder must be a full path");
		Check(new TInvestOptions { ArchiveCacheFolder = "   " }, false, "ArchiveCacheFolder must be a full path");
		Check(new TInvestOptions { ArchiveCacheFolder = "relative\\folder" }, false, "ArchiveCacheFolder must be a full path");
		Check(new TInvestOptions { ArchiveCacheFolder = Folder + "\0" }, false, "ArchiveCacheFolder must be a full path");
		Check(new TInvestOptions { ArchiveCacheFolder = Folder + new string('x', 241) }, false, "ArchiveCacheFolder must be a full path");
		foreach (var bad in new[] { "in?valid", "in*valid", "in:valid", "in\"valid", "in<valid", "in|valid" })
			Check(new TInvestOptions { ArchiveCacheFolder = Path.Combine(Folder, bad) }, false, "ArchiveCacheFolder must be a full path");
		foreach (var bad in new[] { @"\\bad*server\share\cache", @"\\server\bad?share\cache", @"\\server:bad\share\cache" })
			Check(new TInvestOptions { ArchiveCacheFolder = bad }, false, "ArchiveCacheFolder must be a full path");
		Check(new TInvestOptions { ArchiveCacheFolder = @"\\server\share\cache" }, true, string.Empty);
	}

	[Test]
	public void BothEndsOfEveryRangeWorkTogether()
	{
		new TInvestOptions { MaxTickDaysPerRequest = 1, MaxTicksPerRequest = 1_000, ArchiveCacheMaxBytes = 100L * 1024 * 1024, ArchiveCacheFolder = Folder }.Validate();
		new TInvestOptions { MaxTickDaysPerRequest = 31, MaxTicksPerRequest = 5_000_000, ArchiveCacheMaxBytes = 1024L * 1024 * 1024 * 1024, ArchiveCacheFolder = Folder }.Validate();
		Assert.Throws<ArgumentException>(() => new TInvestStore(new ScriptedTInvestGateway(), new TInvestOptions { MaxTickDaysPerRequest = 0 }));
	}

	static void Check(TInvestOptions options, bool valid, string message)
	{
		if (valid)
		{
			Assert.DoesNotThrow(options.Validate);
			return;
		}

		Assert.That(Assert.Throws<ArgumentException>(options.Validate)!.Message, Does.Contain(message));
	}
}
