namespace SomeDataProvider.DataStorage.TInvest.Test;

using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class RecentCacheTests
{
	[Test]
	public void ScopeCapacityNeverEvictsAnInFlightOrStillDispatchedProfile()
	{
		var clock = new ManualTimeProvider();
		clock.Advance(TimeSpan.FromDays(20000));
		var budget = new TInvestReconcileBudget(clock, 1);
		var requests = new List<TInvestReconcileBudget.Request>();
		try
		{
			for (var i = 0; i < 1024; i++) requests.Add(budget.BeginRequest("synthetic-profile-" + i, 2));
			Assert.That(Assert.Throws<TInvestRecentRequestException>(() => budget.BeginRequest("synthetic-new-profile", 2))!.Reason, Is.EqualTo("BudgetExhausted"));
			clock.Advance(TimeSpan.FromSeconds(59));
			requests[0].BeforeDispatch(clock.GetUtcNow().AddMinutes(-5), clock.GetUtcNow().AddSeconds(-2));
			requests[0].Dispose();
			clock.Advance(TimeSpan.FromSeconds(1));
			// The first request's start expired, but its actual dispatch did not; all other profiles are still in flight.
			Assert.That(Assert.Throws<TInvestRecentRequestException>(() => budget.BeginRequest("synthetic-new-profile", 2))!.Reason, Is.EqualTo("BudgetExhausted"));
			clock.Advance(TimeSpan.FromSeconds(59));
			using var admitted = budget.BeginRequest("synthetic-new-profile", 2);
			Assert.That(Assert.Throws<TInvestRecentRequestException>(() => budget.BeginRequest("synthetic-another-profile", 2))!.Reason, Is.EqualTo("BudgetExhausted"));
		}
		finally
		{
			foreach (var request in requests) request.Dispose();
		}
	}

	[Test]
	public async Task ExplicitContextCacheIsBoundedAndAnEvictedContextIsNotHydrated()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway
		{
			Resolve = (ticker, code, _) => Task.FromResult<TInvestInstrument?>(ScriptedTInvestGateway.Invent(ticker, code)),
		};
		using var store = new TInvestStore(gateway, new(), clock);
		for (var i = 0; i < 4097; i++) await store.GetContextInstrumentAsync("SYNTH" + i, "TESTCC", default);
		var retained = Enumerable.Range(0, 4097).Select(i => store.GetCachedContextInstrument("SYNTH" + i, "TESTCC")).ToArray();
		Assert.That(retained.Count(i => i != null), Is.EqualTo(4096));
		var evicted = Array.FindIndex(retained, i => i == null);
		Assert.That(store.GetCachedContextInstrument("SYNTH" + evicted, "TESTCC"), Is.Null);
		Assert.That(gateway.Lookups, Has.Count.EqualTo(4097));
		Assert.That(gateway.ArchiveRequests, Is.Empty);
		Assert.That(gateway.CandleRequests, Is.Empty);
	}

	[Test]
	public void DefaultRecentRequestCeilingIsSix() => Assert.That(new TInvestOptions().LiveReconcileRequestsPerMinute, Is.EqualTo(6));
}
