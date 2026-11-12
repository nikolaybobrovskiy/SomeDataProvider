namespace SomeDataProvider.DataStorage.TInvest.Test;

using Grpc.Core;
using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;
using Tinkoff.InvestApi.V1;

[TestFixture]
public class RecentDispatchTests
{
	[TestCase(1)]
	[TestCase(12)]
	public void ReconnectsAndFailedRequestsCannotResetTheRollingRequestLedger(int ceiling)
	{
		var clock = Clock();
		var budget = new TInvestReconcileBudget(clock, ceiling);
		for (var i = 0; i < ceiling; i++) budget.BeginRequest("synthetic-profile", 2).Dispose();
		clock.Advance(TimeSpan.FromSeconds(59));
		Assert.That(Assert.Throws<TInvestRecentRequestException>(() => budget.BeginRequest("synthetic-profile", 2))!.Reason, Is.EqualTo("BudgetExhausted"));
		clock.Advance(TimeSpan.FromSeconds(1));
		using var nextGeneration = budget.BeginRequest("synthetic-profile", 2);
	}

	[Test]
	public void OverlappingGenerationsShareOneSlotAndBusyRequestsSpendTheSameBudget()
	{
		var clock = Clock();
		var budget = new TInvestReconcileBudget(clock, 2);
		using (var oldGeneration = budget.BeginRequest("synthetic-profile", 2))
		{
			Assert.That(Assert.Throws<TInvestRecentRequestException>(() => budget.BeginRequest("synthetic-profile", 2))!.Reason, Is.EqualTo("ReconcileBusy"));
			oldGeneration.BeforeDispatch(clock.GetUtcNow().AddMinutes(-5), clock.GetUtcNow().AddSeconds(-2));
			Assert.That(oldGeneration.Attempts, Is.EqualTo(1));
		}
		Assert.That(Assert.Throws<TInvestRecentRequestException>(() => budget.BeginRequest("synthetic-profile", 2))!.Reason, Is.EqualTo("BudgetExhausted"));
		using var separateProfile = budget.BeginRequest("synthetic-other-profile", 2);
	}

	[TestCase(1)]
	[TestCase(12)]
	public void LateOldGenerationAttemptsAndNewGenerationShareActualDispatchCeiling(int ceiling)
	{
		var clock = Clock();
		var budget = new TInvestReconcileBudget(clock, ceiling);
		// Starts at t=0 expire first, but their delayed dispatches must stay in the separate ledger.
		for (var i = 0; i < ceiling; i++)
		{
			using var oldGeneration = budget.BeginRequest("synthetic-profile", 2);
			if (i == 0) clock.Advance(TimeSpan.FromSeconds(1));
			oldGeneration.BeforeDispatch(clock.GetUtcNow().AddMinutes(-5), clock.GetUtcNow().AddSeconds(-2));
			oldGeneration.BeforeDispatch(clock.GetUtcNow().AddMinutes(-5), clock.GetUtcNow().AddSeconds(-2));
		}
		clock.Advance(TimeSpan.FromSeconds(59));
		using var newGeneration = budget.BeginRequest("synthetic-profile", 2);
		var from = clock.GetUtcNow().AddMinutes(-5);
		var to = clock.GetUtcNow().AddSeconds(-2);
		Assert.That(Assert.Throws<TInvestRecentRequestException>(() => newGeneration.BeforeDispatch(from, to))!.Reason, Is.EqualTo("BudgetExhausted"));
		Assert.That(newGeneration.Attempts, Is.Zero);
		clock.Advance(TimeSpan.FromSeconds(1));
		newGeneration.BeforeDispatch(from, to);
		Assert.That(newGeneration.Attempts, Is.EqualTo(1));
	}

	[TestCase(1, false)]
	[TestCase(2, false)]
	[TestCase(2, true)]
	public async Task RealSdkBoundaryCountsEveryAttemptAndPreservesExactRequest(int maximumAttempts, bool failsAgain)
	{
		var clock = Clock();
		var limiter = new TInvestRateLimiter(clock);
		var invoker = new RecentInvoker((_, n) => n == 1 || failsAgain
			? throw new RpcException(new Status(StatusCode.ResourceExhausted, "Synthetic quota"), new Metadata { { "x-ratelimit-reset", "1" } })
			: new GetLastTradesResponse());
		using var gateway = new SdkTInvestGateway(invoker, new(), limiter, clock);
		var budget = new TInvestReconcileBudget(clock, 6);
		using var request = budget.BeginRequest("synthetic-profile", maximumAttempts);
		var from = clock.GetUtcNow().AddMinutes(-5);
		var to = clock.GetUtcNow().AddSeconds(-2);
		var task = gateway.GetRecentTradesAsync("synthetic-uid", from, to, maximumAttempts, () => request.BeforeDispatch(from, to), clock.GetUtcNow().AddSeconds(60), default);
		Assert.That(invoker.Calls, Is.EqualTo(1));
		if (maximumAttempts == 1)
		{
			Assert.ThrowsAsync<TInvestRequestException>(async () => await task);
		}
		else
		{
			clock.Advance(TimeSpan.FromSeconds(1));
			if (failsAgain) Assert.ThrowsAsync<TInvestRequestException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
			else Assert.That(await task.WaitAsync(TimeSpan.FromSeconds(5)), Is.Empty);
		}
		Assert.That(request.Attempts, Is.EqualTo(maximumAttempts));
		Assert.That(invoker.Calls, Is.EqualTo(maximumAttempts));
		foreach (var sent in invoker.Requests.Cast<GetLastTradesRequest>())
		{
			Assert.That(sent.From.ToDateTimeOffset(), Is.EqualTo(from));
			Assert.That(sent.To.ToDateTimeOffset(), Is.EqualTo(to));
			Assert.That(sent.TradeSource, Is.EqualTo(TradeSourceType.TradeSourceExchange));
		}
		Assert.That(limiter.InspectTraffic().MarketData, Has.Count.EqualTo(maximumAttempts));
	}

	[Test]
	public async Task ActualSdkLateRetryCannotExceedTheAggregateDispatchLedger()
	{
		var clock = Clock();
		var invoker = new RecentInvoker((_, n) => n is 1 or 3
			? throw new RpcException(new Status(StatusCode.ResourceExhausted, "Synthetic quota"), new Metadata { { "x-ratelimit-reset", n == 1 ? "59" : "1" } })
			: new GetLastTradesResponse());
		using var gateway = new SdkTInvestGateway(invoker, new(), new(clock), clock);
		var budget = new TInvestReconcileBudget(clock, 1);
		var from = clock.GetUtcNow().AddMinutes(-5);
		var to = clock.GetUtcNow().AddSeconds(-2);
		using (var oldGeneration = budget.BeginRequest("synthetic-profile", 2))
		{
			var task = gateway.GetRecentTradesAsync("synthetic-uid", from, to, 2, () => oldGeneration.BeforeDispatch(from, to), clock.GetUtcNow().AddSeconds(60), default);
			clock.Advance(TimeSpan.FromSeconds(59));
			Assert.That(await task.WaitAsync(TimeSpan.FromSeconds(5)), Is.Empty);
			Assert.That(oldGeneration.Attempts, Is.EqualTo(2));
		}
		clock.Advance(TimeSpan.FromSeconds(1));
		using var newGeneration = budget.BeginRequest("synthetic-profile", 2);
		var next = gateway.GetRecentTradesAsync("synthetic-uid", from, to, 2, () => newGeneration.BeforeDispatch(from, to), clock.GetUtcNow().AddSeconds(60), default);
		clock.Advance(TimeSpan.FromSeconds(1));
		Assert.That(Assert.ThrowsAsync<TInvestRecentRequestException>(async () => await next.WaitAsync(TimeSpan.FromSeconds(5)))!.Reason, Is.EqualTo("BudgetExhausted"));
		Assert.That(newGeneration.Attempts, Is.EqualTo(1));
		Assert.That(invoker.Calls, Is.EqualTo(3), "The fourth call is refused at the real dispatch boundary.");
	}

	[Test]
	public async Task RealSdkDispatchRechecksTheWindowAfterSharedLimiterPreparation()
	{
		var clock = Clock();
		var invoker = new RecentInvoker((_, _) => new GetLastTradesResponse());
		var limiter = new TInvestRateLimiter(clock);
		using var gateway = new SdkTInvestGateway(invoker, new(), limiter, clock);
		var budget = new TInvestReconcileBudget(clock, 6);
		using var request = budget.BeginRequest("synthetic-profile", 2);
		var from = clock.GetUtcNow().AddMinutes(-58);
		var to = clock.GetUtcNow().AddSeconds(-2);
		limiter.Correct(TInvestLimitGroup.MarketData, null, 0, TimeSpan.FromSeconds(1), true);
		var task = gateway.GetRecentTradesAsync("synthetic-uid", from, to, 2, () => request.BeforeDispatch(from, to), clock.GetUtcNow().AddSeconds(60), default);
		Assert.That(invoker.Calls, Is.Zero);
		clock.Advance(TimeSpan.FromSeconds(1));
		Assert.That(Assert.ThrowsAsync<TInvestRecentRequestException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)))!.Reason, Is.EqualTo("RecentWindowExpired"));
		Assert.That(invoker.Calls, Is.Zero);
		Assert.That(request.Attempts, Is.Zero);
		await Task.CompletedTask;
	}

	[Test]
	public void RecentSdkRejectsAResponseForAnotherInstrument()
	{
		var clock = Clock();
		var from = clock.GetUtcNow().AddMinutes(-5);
		var to = clock.GetUtcNow().AddSeconds(-2);
		var invoker = new RecentInvoker((_, _) => new GetLastTradesResponse
		{
			Trades = { new Trade { InstrumentUid = "synthetic-other-uid", Time = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(from), Price = new Quotation { Units = 100 }, Quantity = 1, TradeSource = TradeSourceType.TradeSourceExchange } },
		});
		using var gateway = new SdkTInvestGateway(invoker, new(), new(clock), clock);
		using var store = new TInvestStore(gateway, new(), clock);
		using var budget = store.ReconcileBudget.BeginRequest("synthetic-profile", 2);
		Assert.That(Assert.ThrowsAsync<TInvestRecentRequestException>(() => store.CreateRecentTickReaderAsync(new(ScriptedTInvestGateway.Invent()), from, to, 1000, 2, budget, clock.GetUtcNow().AddSeconds(60)))!.Reason, Is.EqualTo("SourceUnavailable"));
	}

	static ManualTimeProvider Clock()
	{
		var clock = new ManualTimeProvider();
		clock.Advance(TimeSpan.FromDays(20000));
		return clock;
	}

	sealed class RecentInvoker(Func<string, int, object> script) : CallInvoker
	{
		public int Calls { get; private set; }
		public List<object> Requests { get; } = new();

		public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
		{
			Calls++;
			Requests.Add(request!);
			Task<TResponse> response;
			try { response = Task.FromResult((TResponse)script(method.Name, Calls)); }
			catch (RpcException exception) { response = Task.FromException<TResponse>(exception); }
			return new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
		}

		public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
		public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
	}
}
