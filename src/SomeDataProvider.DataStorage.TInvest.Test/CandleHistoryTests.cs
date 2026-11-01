namespace SomeDataProvider.DataStorage.TInvest.Test;

using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class CandleHistoryTests
{
	static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

	public static TInvestCandle Candle(DateTimeOffset time, bool complete = true, bool exchange = true) => new(time, 11, 14, 10, 12, 7, complete, exchange);

	[TestCase(5, 2500, 200, 0)]
	[TestCase(10, 1250, 200, 0)]
	[TestCase(30, 2500, 1200, 0)]
	[TestCase(60, 2400, 1440, 0)]
	[TestCase(120, 1200, 1440, 0)]
	[TestCase(180, 750, 1440, 0)]
	[TestCase(300, 2400, 10080, 0)]
	[TestCase(600, 1200, 10080, 0)]
	[TestCase(900, 2400, 30240, 0)]
	[TestCase(1800, 1200, 30240, 0)]
	[TestCase(3600, 2400, 0, 3)]
	[TestCase(7200, 2400, 0, 3)]
	[TestCase(14400, 700, 0, 3)]
	[TestCase(86400, 2400, 0, 72)]
	[TestCase(604800, 300, 0, 60)]
	[TestCase(2592000, 120, 0, 120)]
	public void IntervalAvailabilityAndCaps(int seconds, int limit, int minutes, int months)
	{
		var interval = TInvestCandleInterval.FromSeconds(seconds);
		Assert.That(interval.Limit, Is.EqualTo(limit));
		Assert.That(interval.MaximumMonths, Is.EqualTo(months));
		Assert.That(interval.MaximumPeriod, Is.EqualTo(minutes == 0 ? (TimeSpan?)null : TimeSpan.FromMinutes(minutes)));
		Assert.That(interval.SecondsOnly, Is.EqualTo(seconds < 60));
		var plan = Plan(seconds, Now.AddDays(-1).UtcDateTime, Now.UtcDateTime);
		Assert.That(plan.Calls.All(c => c.Limit == limit && c.End <= interval.MaximumEnd(c.Start)), Is.True);
	}

	[TestCase(0)]
	[TestCase(1)]
	[TestCase(2)]
	[TestCase(4)]
	[TestCase(20)]
	public void UnsupportedIntervalsRejectBeforeAnyCall(int seconds) => Assert.Throws<TInvestRequestException>(() => Plan(seconds, Now.AddDays(-1).UtcDateTime, Now.UtcDateTime));

	[TestCase(60, 3)]
	[TestCase(300, 3)]
	[TestCase(5, 14)]
	public void IntradaySplitsAtMidnightAndPeriodCaps(int seconds, int expectedCalls)
	{
		var plan = Plan(seconds, Now.AddDays(-2).AddHours(6).UtcDateTime, Now.UtcDateTime);
		Assert.That(plan.Calls.Count, Is.EqualTo(expectedCalls));
		Assert.That(plan.Calls.First().Start, Is.EqualTo(plan.Start));
		Assert.That(plan.Calls.Last().End, Is.EqualTo(plan.End));
		for (var i = 0; i < plan.Calls.Count; i++)
		{
			var call = plan.Calls[i];
			Assert.That(call.End.UtcDateTime.Date <= call.Start.UtcDateTime.Date.AddDays(1), Is.True);
			Assert.That(call.End - call.Start, Is.LessThanOrEqualTo(plan.Interval.MaximumPeriod!.Value));
			if (i > 0) Assert.That(call.Start, Is.EqualTo(plan.Calls[i - 1].End));
		}
	}

	[TestCase(5)]
	[TestCase(10)]
	[TestCase(30)]
	public void SecondCandlesRejectBeforeClampingOldStart(int seconds)
	{
		var recent = new TInvestSymbol(ScriptedTInvestGateway.Invent() with { FirstMinuteCandle = Now.AddDays(-1) });
		Assert.Throws<TInvestRequestException>(() => TInvestCandlePlan.Create(recent, seconds, Now.AddDays(-30).AddSeconds(-1).UtcDateTime, Now.UtcDateTime, Now, new()));
		Assert.That(Plan(seconds, Now.AddDays(-30).UtcDateTime, Now.UtcDateTime, new TInvestOptions { MaxCandleCallsPerRequest = 400 }).Calls.Count, Is.GreaterThan(0));
	}

	[TestCase(5)]
	[TestCase(10)]
	[TestCase(30)]
	public void ZeroSecondStartSelectsEarliestRetainedCandleWithOldRecentOrMissingMetadata(int seconds)
	{
		foreach (var first in new DateTimeOffset?[] { null, Now.AddYears(-5), Now.AddDays(-4) })
		{
			foreach (var now in new[] { Now, Now.AddMilliseconds(250) })
			{
				var symbol = new TInvestSymbol(ScriptedTInvestGateway.Invent() with { FirstMinuteCandle = first });
				var plan = TInvestCandlePlan.Create(symbol, seconds, default, default, now, new());
				var earliest = Now.AddDays(-30).AddSeconds(now == Now ? 0 : 1);
				Assert.That(plan.Start, Is.EqualTo(first > earliest ? first.Value : earliest));
				Assert.That(plan.Calls.Count, Is.InRange(1, 400));
			}
		}
	}

	[Test]
	public void WholeSecondsHalfOpenZerosFutureEndAndAvailability()
	{
		var first = Now.AddDays(-4);
		var symbol = new TInvestSymbol(ScriptedTInvestGateway.Invent() with { FirstMinuteCandle = first, FirstDailyCandle = first.AddDays(-100) });
		var plan = TInvestCandlePlan.Create(symbol, 60, default, default, Now, new());
		Assert.That(plan.Start, Is.EqualTo(first));
		Assert.That(plan.End, Is.EqualTo(Now));
		Assert.That(plan.OpenEnded, Is.True);
		Assert.That(TInvestCandlePlan.Create(symbol, 86400, default, default, Now, new()).Start, Is.EqualTo(first.AddDays(-100)));
		Assert.That(TInvestCandlePlan.Create(symbol, 60, first.AddDays(-10).UtcDateTime, Now.AddDays(1).UtcDateTime, Now, new()).Start, Is.EqualTo(first));
		Assert.Throws<TInvestRequestException>(() => Plan(60, first.AddTicks(1).UtcDateTime, Now.UtcDateTime));
		Assert.Throws<TInvestRequestException>(() => Plan(60, first.UtcDateTime, Now.AddTicks(-1).UtcDateTime));
		Assert.Throws<TInvestRequestException>(() => Plan(60, Now.UtcDateTime, Now.UtcDateTime));
		Assert.Throws<TInvestRequestException>(() => Plan(60, default, default));
	}

	[TestCase(1, false)]
	[TestCase(1, true)]
	[TestCase(400, false)]
	[TestCase(400, true)]
	public void SettingRangeEndsAndBooleanCombinations(int cap, bool dealer)
	{
		var options = new TInvestOptions { MaxCandleCallsPerRequest = cap, IncludeDealerTrades = dealer };
		options.Validate();
		Assert.That(Plan(60, Now.AddHours(-1).UtcDateTime, Now.UtcDateTime, options).Calls.Single().IncludeDealer, Is.EqualTo(dealer));
	}

	[TestCase(0)]
	[TestCase(401)]
	public void InvalidCallCapRejectedAtStartup(int cap) => Assert.Throws<ArgumentException>(() => new TInvestOptions { MaxCandleCallsPerRequest = cap }.Validate());

	[Test]
	public void AnnualRequestFits400ButLongerRangeIsRejected()
	{
		Assert.That(Plan(60, Now.AddDays(-365).UtcDateTime, Now.UtcDateTime).Calls.Count, Is.EqualTo(366));
		Assert.Throws<TInvestRequestException>(() => Plan(60, Now.AddDays(-401).UtcDateTime, Now.UtcDateTime));
	}

	[Test]
	public void InvalidDealerSettingRejectedByStartupBinding()
	{
		var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [nameof(TInvestOptions.IncludeDealerTrades)] = "not-a-boolean" }).Build();
		var error = Assert.Throws<InvalidOperationException>(() => config.Get<TInvestOptions>());
		Assert.That(error!.Message, Does.Contain(nameof(TInvestOptions.IncludeDealerTrades)));
	}

	[Test]
	public async Task FullMonthlyPageContinuesWithoutRepeatingBoundaryCandle()
	{
		var clock = Clock();
		using var gateway = new ScriptedTInvestGateway { Limiter = new(clock) };
		gateway.Candles = (request, _) => Task.FromResult<IReadOnlyList<TInvestCandle>>(Enumerable.Range(0, request.Limit)
			.Select(i => Candle(request.Start.AddMonths(i))).Where(c => c.Time < request.End)
			.Append(Candle(request.Start.AddMonths(-1))).Append(Candle(request.End)).ToArray());
		using var store = new TInvestStore(gateway, new(), clock);
		using var reader = await store.CreateCandleReaderAsync(Symbol(), 2592000, new DateTime(2015, 1, 1), new DateTime(2026, 1, 1), Now.AddSeconds(20));
		var first = await reader.ReadSymbolHistoryAsync();
		var second = await reader.ReadSymbolHistoryAsync();
		Assert.That(first.Records.Count, Is.EqualTo(120));
		Assert.That(second.Records.Count, Is.EqualTo(12));
		Assert.That(first.HasMore, Is.True);
		Assert.That(second.HasMore, Is.False);
		Assert.That(first.Records.Concat(second.Records).Select(r => r.TimeStamp).Distinct().Count(), Is.EqualTo(132));
		Assert.That(gateway.CandleRequests[0].End, Is.EqualTo(gateway.CandleRequests[1].Start));
	}

	[Test]
	public async Task ReaderDisposalIsIdempotentAndLaterReadsFailClearly()
	{
		var clock = Clock();
		using var gateway = new ScriptedTInvestGateway { Limiter = new(clock) };
		using var store = new TInvestStore(gateway, new(), clock);
		var reader = await store.CreateCandleReaderAsync(Symbol(), 60, Now.AddHours(-1).UtcDateTime, default, Now.AddSeconds(20));
		reader.Dispose();
		Assert.DoesNotThrow(reader.Dispose);
		Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadSymbolHistoryAsync());
	}

	[TestCase(false, false)]
	[TestCase(true, false)]
	[TestCase(false, true)]
	[TestCase(true, true)]
	public async Task ReaderFiltersSortsBoundsDealerAndOnlyNewestIncomplete(bool open, bool dealer)
	{
		var clock = Clock();
		using var gateway = new ScriptedTInvestGateway { Limiter = new(clock) };
		var from = Now.AddMinutes(-5);
		gateway.Candles = (_, _) => Task.FromResult<IReadOnlyList<TInvestCandle>>(new[]
		{
			Candle(from.AddMinutes(4), complete: false), Candle(from.AddMinutes(3)),
			Candle(from.AddMinutes(1), exchange: false), Candle(from), Candle(from.AddMinutes(-1)), Candle(Now),
			Candle(from.AddMinutes(2), complete: false), Candle(from),
		});
		using var store = new TInvestStore(gateway, new() { IncludeDealerTrades = dealer }, clock);
		using var reader = await store.CreateCandleReaderAsync(Symbol(), 60, from.UtcDateTime, open ? default : Now.UtcDateTime, Now.AddSeconds(20));
		var result = await reader.ReadSymbolHistoryAsync();
		var times = new List<DateTime> { from.UtcDateTime };
		if (dealer) times.Add(from.AddMinutes(1).UtcDateTime);
		times.Add(from.AddMinutes(3).UtcDateTime);
		if (open) times.Add(from.AddMinutes(4).UtcDateTime);
		Assert.That(result.Records.Select(r => r.TimeStamp), Is.EqualTo(times));
		Assert.That(result.Records.All(r => r.Volume == 70 && r.OpenPrice == 11 && r.LastPrice == 12), Is.True);
		Assert.That(result.HasMore, Is.False);
	}

	[Test]
	public async Task ReaderDeadlineCancelsSourceWithFakeClock()
	{
		var clock = Clock();
		using var gateway = new ScriptedTInvestGateway { Limiter = new(clock) };
		gateway.Candles = (_, ct) => new TaskCompletionSource<IReadOnlyList<TInvestCandle>>().Task.WaitAsync(ct);
		using var store = new TInvestStore(gateway, new(), clock);
		using var reader = await store.CreateCandleReaderAsync(Symbol(), 60, Now.AddHours(-1).UtcDateTime, default, Now.AddSeconds(20));
		var task = reader.ReadSymbolHistoryAsync();
		clock.Advance(TimeSpan.FromSeconds(20));
		var error = Assert.ThrowsAsync<TInvestRequestException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
	}

	static TInvestSymbol Symbol() => new(ScriptedTInvestGateway.Invent());
	static ManualTimeProvider Clock()
	{
		var clock = new ManualTimeProvider();
		clock.Advance(Now - DateTimeOffset.UnixEpoch);
		return clock;
	}
	static TInvestCandlePlan Plan(int seconds, DateTime start, DateTime end, TInvestOptions? options = null) => TInvestCandlePlan.Create(Symbol(), seconds, start, end, Now, options ?? new());
}
