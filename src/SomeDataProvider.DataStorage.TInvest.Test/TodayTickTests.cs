namespace SomeDataProvider.DataStorage.TInvest.Test;

using NUnit.Framework;
using static SomeDataProvider.DataStorage.TInvest.Test.TickFixture;

[TestFixture]
public class TodayTickTests
{
	[Test]
	public async Task RecordedTradesAreMergedWithTheLastHourAsAMultisetAndDealerTradesAreDropped()
	{
		using var fixture = await Covered();
		var a = Trade(Utc(10, 30, 9, 30), 0, 1, 1);
		var b = Trade(Utc(10, 30, 11, 30), 0, 2, 1);
		var c = Trade(Utc(10, 30, 11, 45), 250_000_000, 3, 1);
		fixture.Recording.Trades.AddRange(new[] { a, b, c });
		var d = Trade(Utc(10, 30, 11, 59, 30), 0, 4, 1);
		fixture.Gateway.LastTrades = (_, _) => new[] { b, c, c, d, Trade(Utc(10, 30, 11, 50), 0, 9, 1, exchange: false) };
		var reader = await fixture.Ticks(Utc(10, 30, 9));
		var ticks = await ReadAll(reader);
		Assert.That(ticks.Select(t => t.LastPrice), Is.EqualTo(new[] { 1d, 2, 3, 3, 4 }), "b and c are counted once per source; the second c exists only in the answer.");
		Assert.That(ticks.Select(t => t.Microseconds), Is.Ordered);
		Assert.That(ticks[2].Microseconds % 1_000_000, Is.EqualTo(250_000));
		var request = fixture.Gateway.LastTradeRequests.Single();
		Assert.That(request.To - request.From, Is.EqualTo(TimeSpan.FromHours(1)));
	}

	[Test]
	public async Task EqualTradesInOneSourceStayDistinct()
	{
		using var fixture = await Covered();
		var t = Trade(Utc(10, 30, 11, 30), 0, 2, 1);
		fixture.Recording.Trades.AddRange(new[] { t, t });
		fixture.Gateway.LastTrades = (_, _) => new[] { t };
		Assert.That(await ReadAll(await fixture.Ticks(Utc(10, 30, 9))), Has.Count.EqualTo(2));
	}

	[Test]
	public void MultisetMergeKeepsTheLargerMultiplicityAndOrdersStably()
	{
		var x = new TInvestTick(new(100, 0), 1, 1, TInvestDirection.Buy);
		var y = new TInvestTick(new(100, 0), 2, 1, TInvestDirection.Buy);
		var z = new TInvestTick(new(99, 0), 3, 1, TInvestDirection.Sell);
		Assert.That(TInvestStore.Merge(new() { x, x }, new() { y, x, x, x, z }), Is.EqualTo(new[] { z, x, x, y, x }));
		Assert.That(TInvestStore.Merge(new(), new()), Is.Empty);
	}

	[Test]
	public async Task AnUncoveredSpanRejectsAndNamesItWithTheStartOfRecording()
	{
		using var fixture = new TickFixture();
		fixture.Recording.Intervals.Add((At(10), At(11, 30)));
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 30, 9)));
		Assert.That(error!.Message, Is.EqualTo("T-Invest ticks for 09:00:00\u201310:00:00 UTC today are not available; the bridge recorded this instrument from 10:00:00."));
		// The last hour covers the rest: a start inside recorded coverage works.
		await ReadAll(await fixture.Ticks(Utc(10, 30, 10)));
	}

	[Test]
	public void NothingRecordedTodayOnlyTheLastHourIsServed()
	{
		using var fixture = new TickFixture();
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 30, 10)));
		Assert.That(error!.Message, Does.Contain("10:00:00\u201311:00:00 UTC today").And.Contain("has not recorded this instrument today"));
		Assert.DoesNotThrowAsync(() => fixture.Ticks(Utc(10, 30, 11, 0, 1)));
	}

	[Test]
	public async Task AHoleAfterTheRequestedEndDoesNotRejectAClosedRequest()
	{
		using var fixture = new TickFixture();
		fixture.Recording.Intervals.Add((At(9), At(10)));
		// Closed 09:00-10:00 succeeds without asking for the last hour.
		var reader = await fixture.Ticks(Utc(10, 30, 9), Utc(10, 30, 10));
		Assert.That(reader.Total, Is.EqualTo(0));
		Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
		// An open-ended request from 09:00 reaches the frontier and names the hole.
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 30, 9)));
		Assert.That(error!.Message, Does.Contain("10:00:00\u201311:00:00 UTC today"));
		// An end in the future is open-ended too, so it is checked up to the frontier only.
		Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 30, 9), Utc(10, 31, 9)));
		await Task.CompletedTask;
	}

	[Test]
	public async Task ClosedEndInsideTheLastHourStillUsesItsAnswer()
	{
		using var fixture = new TickFixture();
		fixture.Gateway.LastTrades = (_, _) => new[] { Trade(Utc(10, 30, 11, 30), 0, 2, 1), Trade(Utc(10, 30, 11, 50), 0, 3, 1) };
		var ticks = await ReadAll(await fixture.Ticks(Utc(10, 30, 11, 10), Utc(10, 30, 11, 40)));
		Assert.That(ticks.Select(t => t.LastPrice), Is.EqualTo(new[] { 2d }), "Half-open end: the 11:50 trade is outside.");
	}

	[Test]
	public void RecordedDaysCannotMixInDealerTrades()
	{
		using var fixture = new TickFixture(dealer: true);
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 30, 11, 30)));
		Assert.That(error!.Message, Does.Contain("without dealer trades"));
	}

	[Test]
	public async Task ARangeAcrossAnArchiveAndTodayIsOneOrderedAnswer()
	{
		using var fixture = await Covered(0);
		fixture.Archive(10, 29, Gzip(Csv(Row("2026-10-29T20:00:00Z", "SELL", "7", 1))));
		fixture.Recording.Trades.Add(Trade(Utc(10, 30, 8), 0, 8, 1));
		var ticks = await ReadAll(await fixture.Ticks(Utc(10, 29, 12)));
		Assert.That(ticks.Select(t => t.LastPrice), Is.EqualTo(new[] { 7d, 8 }));
		Assert.That(ticks.Select(t => t.Direction), Is.EqualTo(new[] { TInvestDirection.Sell, TInvestDirection.Buy }));
	}

	[TestCase(-1)]
	[TestCase(0)]
	public void FirstGapReturnsNullOnlyWhenTheUnionCoversTheSpan(int shift)
	{
		var a = At(9);
		var b = At(10);
		Assert.That(TInvestStore.FirstGap(a, b, new[] { (a, At(9, 30)), (At(9, 30), b) }), Is.Null);
		Assert.That(TInvestStore.FirstGap(a, b, new[] { (a.AddMinutes(shift), At(9, 20)), (At(9, 30), b) }), Is.EqualTo((At(9, 20), At(9, 30))));
		Assert.That(TInvestStore.FirstGap(a, b, Array.Empty<(DateTimeOffset, DateTimeOffset)>()), Is.EqualTo((a, b)));
		Assert.That(TInvestStore.FirstGap(a, b, new[] { (At(9, 40), At(11)) }), Is.EqualTo((a, At(9, 40))));
	}

	static DateTimeOffset At(int hour, int minute = 0, int second = 0) => new(2026, 10, 30, hour, minute, second, TimeSpan.Zero);

	static async Task<TickFixture> Covered(int fromHour = 9, int fromMinute = 0)
	{
		var fixture = new TickFixture();
		fixture.Recording.Intervals.Add((At(fromHour, fromMinute), At(11, 59, 59)));
		await Task.CompletedTask;
		return fixture;
	}
}

[TestFixture]
public class TickBudgetTests
{
	[Test]
	public async Task AColdRangeThatDoesNotFitTheArchiveWindowAnswersRetryInAndFillsInTheBackground()
	{
		using var fixture = new TickFixture();
		fixture.Archive(10, 28, Gzip(Csv(Row("2026-10-28T07:00:00Z", "BUY", "1", 1))));
		for (var i = 0; i < 27; i++) Assert.That(fixture.Gateway.Limiter.TryAcquire(TInvestLimitGroup.Archives), Is.EqualTo(TimeSpan.Zero));
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 28), Utc(10, 29)));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		Assert.That(error.Message, Does.Contain("retry in 5 s"));
		Assert.That(fixture.Gateway.ArchiveRequests, Is.Empty, "No stall and no early request.");
		fixture.Clock.Advance(TimeSpan.FromSeconds(61));
		await fixture.Store.BackgroundFills.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.That(fixture.Gateway.ArchiveRequests, Has.Count.EqualTo(1));
		Assert.That(await ReadAll(await fixture.Ticks(Utc(10, 28), Utc(10, 29))), Has.Count.EqualTo(1));
		Assert.That(fixture.Gateway.ArchiveRequests, Has.Count.EqualTo(1), "The retry finds the cached file.");
	}

	[Test]
	public async Task TwoColdRequestsCannotBothReserveMoreThanTheWindowHolds()
	{
		using var fixture = new TickFixture();
		fixture.Gateway.Instruments.Add(ScriptedTInvestGateway.Invent("SYNTHB", "TESTCC") with { FirstMinuteCandle = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) });
		var other = (await fixture.Store.ResolveAsync("SYNTHB", "TESTCC", fixture.Clock.GetUtcNow().AddSeconds(8)))!;
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Gateway.BeforeArchive = () =>
		{
			started.TrySetResult();
			return gate.Task;
		};
		var first = fixture.Store.CreateTickReaderAsync(fixture.Symbol, Utc(10, 9), Utc(10, 29), fixture.Deadline());
		await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Store.CreateTickReaderAsync(other, Utc(10, 9), Utc(10, 29), fixture.Deadline()));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		Assert.That(error.Message, Does.Contain("retry in"));
		gate.SetResult();
		for (var step = 0; step < 100 && !first.IsCompleted; step++)
		{
			// Reserved calls are paced 25 ms apart on the fake clock.
			fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
			await Task.WhenAny(first, Task.Delay(20));
		}

		Assert.That((await first.WaitAsync(TimeSpan.FromSeconds(10))).Total, Is.EqualTo(0));
	}

	[Test]
	public async Task SlowDownloadsPastThePreHeaderBudgetAnswerRetryInAndFinishInTheBackground()
	{
		using var fixture = new TickFixture();
		fixture.Archive(10, 28, Gzip(Csv(Row("2026-10-28T07:00:00Z", "BUY", "1", 1))));
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Gateway.BeforeArchive = () =>
		{
			started.TrySetResult();
			return gate.Task;
		};
		var request = fixture.Ticks(Utc(10, 28), Utc(10, 29));
		await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
		fixture.Clock.Advance(TimeSpan.FromSeconds(21));
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => request);
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		Assert.That(error.Message, Does.Contain("retry in"));
		gate.SetResult();
		await fixture.Store.BackgroundFills.WaitAsync(TimeSpan.FromSeconds(10));
		fixture.Gateway.BeforeArchive = null;
		Assert.That(await ReadAll(await fixture.Ticks(Utc(10, 28), Utc(10, 29))), Has.Count.EqualTo(1));
		Assert.That(fixture.Gateway.ArchiveRequests, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task MoreColdDaysThanOneWindowHoldsFillInTheBackgroundAcrossRetries()
	{
		using var fixture = new TickFixture();
		for (var day = 1; day <= 29; day++) fixture.Archive(10, day, Gzip(Csv(Row($"2026-10-{day:00}T07:00:00Z", "BUY", "1", 1))));
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 1), Utc(10, 30)));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		Assert.That(error.Message, Does.Contain("retry in 67 s"), "29 files at 27 a minute: 65 s + 2 s, capped at 300.");
		var fills = fixture.Store.BackgroundFills;
		for (var step = 0; step < 20 && !fills.IsCompleted; step++)
		{
			// Fake time moves in steps while the two background workers wait for the next window.
			fixture.Clock.Advance(TimeSpan.FromSeconds(30));
			await Task.WhenAny(fills, Task.Delay(50));
		}

		await fills.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.That(fixture.Gateway.ArchiveRequests, Has.Count.EqualTo(29));
		Assert.That((await fixture.Ticks(Utc(10, 1), Utc(10, 30))).Total, Is.EqualTo(29));
	}

	[Test]
	public async Task AFailureInOneDayRejectsTheRequestAndIsNotCached()
	{
		using var fixture = new TickFixture();
		var fail = true;
		fixture.Gateway.BeforeArchive = () => fail ? Task.FromException(new TInvestRequestException("T-Invest archive request failed.")) : Task.CompletedTask;
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 28), Utc(10, 29)));
		Assert.That(error!.Message, Does.Contain("archive request failed"));
		fail = false;
		try
		{
			await fixture.Store.BackgroundFills.WaitAsync(TimeSpan.FromSeconds(10));
		}
		catch (TInvestRequestException)
		{
			// The failed fill reports its failure once more here; it is not cached.
		}

		Assert.That((await fixture.Ticks(Utc(10, 28), Utc(10, 29))).Total, Is.EqualTo(0));
	}
}
