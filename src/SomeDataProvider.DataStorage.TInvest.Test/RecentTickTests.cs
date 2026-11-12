namespace SomeDataProvider.DataStorage.TInvest.Test;

using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class RecentTickTests
{
	[TestCase(3480, 2, null)]
	[TestCase(3479, 2, null)]
	[TestCase(3481, 2, "RecentWindowExpired")]
	[TestCase(300, 1, "ClockMismatch")]
	[TestCase(300, 0, "ClockMismatch")]
	public async Task ClosedWindowEdgesAreNeverClamped(int startAge, int endAge, string? reason)
	{
		using var fixture = new RecentFixture();
		var from = fixture.Clock.GetUtcNow().AddSeconds(-startAge);
		var to = fixture.Clock.GetUtcNow().AddSeconds(-endAge);
		if (reason != null)
		{
			var error = Assert.ThrowsAsync<TInvestRecentRequestException>(() => fixture.Read(from, to));
			Assert.That(error!.Reason, Is.EqualTo(reason));
			Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
		}
		else
		{
			Assert.That(await fixture.Read(from, to), Is.Empty);
			Assert.That(fixture.Gateway.LastTradeRequests.Single(), Is.EqualTo((from, to)));
		}
		Assert.That(fixture.Gateway.ArchiveRequests, Is.Empty);
		Assert.That(fixture.Gateway.CandleRequests, Is.Empty);
	}

	[Test]
	public void WindowExpiringDuringPreparationRejectsBeforeAnySourceDispatch()
	{
		using var fixture = new RecentFixture();
		fixture.Gateway.BeforeRecentAdmission = () => fixture.Clock.Advance(TimeSpan.FromSeconds(3));
		var error = Assert.ThrowsAsync<TInvestRecentRequestException>(() => fixture.Read(fixture.Clock.GetUtcNow().AddMinutes(-58), fixture.Clock.GetUtcNow().AddSeconds(-2)));
		Assert.That(error!.Reason, Is.EqualTo("RecentWindowExpired"));
		Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
		Assert.That(fixture.Gateway.ArchiveRequests, Is.Empty);
	}

	[TestCase(1, true)]
	[TestCase(1, false)]
	[TestCase(31, true)]
	[TestCase(31, false)]
	public async Task MidnightUsesOnlyExplicitDatePiecesOrRejectsTheUnplannedSecondDay(int cap, bool split)
	{
		var midnight = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
		using var fixture = new RecentFixture(days: cap, now: midnight.AddMinutes(5));
		var from = midnight.AddMinutes(-5);
		var to = split ? midnight : midnight.AddMinutes(4);
		if (cap == 1 && !split)
		{
			Assert.That(Assert.ThrowsAsync<TInvestRecentRequestException>(() => fixture.Read(from, to))!.Reason, Is.EqualTo("TickDayCap"));
			Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
		}
		else
		{
			Assert.That(await fixture.Read(from, to), Is.Empty);
			Assert.That(fixture.Gateway.LastTradeRequests.Single(), Is.EqualTo((from, to)));
			if (split) Assert.That(await fixture.Read(midnight, midnight.AddMinutes(4)), Is.Empty);
		}
		Assert.That(fixture.Gateway.ArchiveRequests, Is.Empty);
		Assert.That(fixture.Gateway.CandleRequests, Is.Empty);
	}

	[Test]
	public async Task SourceOccurrencesAreKeptWithExactBoundsUnitsAndNoRecorderPrerequisite()
	{
		using var fixture = new RecentFixture();
		var from = fixture.Clock.GetUtcNow().AddMinutes(-5);
		var to = fixture.Clock.GetUtcNow().AddSeconds(-2);
		var trade = new TInvestTrade(fixture.Symbol.Instrument.Short.Uid, new(from.ToUnixTimeSeconds(), 123456000), 101.25, 3, TInvestDirection.Buy, true);
		fixture.Gateway.LastTrades = (_, _) => new[] { trade with { Time = new(to.ToUnixTimeSeconds(), 0) }, trade, trade, trade with { IsExchange = false } };
		var ticks = await fixture.Read(from, to);
		Assert.That(ticks, Has.Count.EqualTo(2));
		Assert.That(ticks.All(t => t.Microseconds == trade.Time.Microseconds && t.Volume == 30 && t.LastPrice == 101.25), Is.True);
	}

	[TestCase(16, 17)]
	[TestCase(1000, 1001)]
	public void SmallerClientOrServerCapRejectsWithoutPartialSuccess(int clientCap, int count)
	{
		using var fixture = new RecentFixture(ticks: 1000);
		var from = fixture.Clock.GetUtcNow().AddMinutes(-5);
		fixture.Gateway.LastTrades = (_, _) => Enumerable.Repeat(new TInvestTrade(fixture.Symbol.Instrument.Short.Uid, new(from.ToUnixTimeSeconds(), 0), 100, 1, TInvestDirection.Buy, true), count).ToArray();
		Assert.That(Assert.ThrowsAsync<TInvestRecentRequestException>(() => fixture.Read(from, fixture.Clock.GetUtcNow().AddSeconds(-2), clientCap))!.Reason, Is.EqualTo("TickCountCap"));
	}

	[Test]
	public void SourceFailureNeverBecomesAnEmptyAnswer()
	{
		using var fixture = new RecentFixture();
		fixture.Gateway.LastTrades = (_, _) => throw new TInvestRequestException("Synthetic source failure.");
		Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Read(fixture.Clock.GetUtcNow().AddMinutes(-5), fixture.Clock.GetUtcNow().AddSeconds(-2)));
		Assert.That(fixture.Gateway.LastTradeRequests, Has.Count.EqualTo(1));
		Assert.That(fixture.Gateway.ArchiveRequests, Is.Empty);
	}

	[TestCase(0)]
	[TestCase(13)]
	public void InvalidRequestCeilingIsRejectedAtStartup(int value) =>
		Assert.That(Assert.Throws<ArgumentException>(() => new TInvestOptions { LiveReconcileRequestsPerMinute = value }.Validate())!.Message, Does.Contain("LiveReconcileRequestsPerMinute must be 1-12"));

	[TestCase(1, 1, 1000)]
	[TestCase(1, 1, 5000000)]
	[TestCase(1, 31, 1000)]
	[TestCase(1, 31, 5000000)]
	[TestCase(12, 1, 1000)]
	[TestCase(12, 1, 5000000)]
	[TestCase(12, 31, 1000)]
	[TestCase(12, 31, 5000000)]
	public async Task SettingRangeEndCombinationsRemainUsable(int quota, int days, int ticks)
	{
		using var fixture = new RecentFixture(quota, days, ticks);
		Assert.That(await fixture.Read(fixture.Clock.GetUtcNow().AddMinutes(-5), fixture.Clock.GetUtcNow().AddSeconds(-2)), Is.Empty);
	}

	sealed class RecentFixture : IDisposable
	{
		public RecentFixture(int quota = 6, int days = 31, int ticks = 5000000, DateTimeOffset? now = null)
		{
			Clock.Advance((now ?? new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)) - DateTimeOffset.UnixEpoch);
			Gateway = new() { Limiter = new(Clock) };
			Store = new(Gateway, new() { LiveReconcileRequestsPerMinute = quota, MaxTickDaysPerRequest = days, MaxTicksPerRequest = ticks }, Clock);
		}
		public ManualTimeProvider Clock { get; } = new();
		public ScriptedTInvestGateway Gateway { get; }
		public TInvestStore Store { get; }
		public TInvestSymbol Symbol { get; } = new(ScriptedTInvestGateway.Invent());
		public async Task<List<TInvestTickRecord>> Read(DateTimeOffset from, DateTimeOffset to, int cap = 5000000)
		{
			using var budget = Store.ReconcileBudget.BeginRequest("synthetic-profile", 2);
			using var reader = await Store.CreateRecentTickReaderAsync(Symbol, from, to, cap, 2, budget, Clock.GetUtcNow().AddSeconds(60));
			return await TickFixture.ReadAll(reader);
		}
		public void Dispose() => Store.Dispose();
	}
}
