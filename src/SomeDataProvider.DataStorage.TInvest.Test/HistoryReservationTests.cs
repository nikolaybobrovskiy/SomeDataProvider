namespace SomeDataProvider.DataStorage.TInvest.Test;

using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class HistoryReservationTests
{
	[Test]
	public void NearlyFullSharedMinuteRejects400CallsBeforeHeader()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		for (var i = 0; i < 539; i++)
		{
			if (i > 0 && i % 45 == 0) clock.Advance(TimeSpan.FromSeconds(1));
			Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.Zero));
		}
		var before = clock.GetUtcNow();
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, before.AddSeconds(20), default));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		Assert.That(clock.GetUtcNow(), Is.EqualTo(before));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.Zero), "Failed reservations release all tentative slots.");
	}

	[Test]
	public async Task TwoConcurrentPlansCannotBothSpendSameQuotaAndDisposalReleasesUnusedSlots()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
		{
			try { return await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, clock.GetUtcNow().AddSeconds(20), default); }
			catch (TInvestRequestException) { return null; }
		})));
		Assert.That(results.Count(r => r != null), Is.EqualTo(1));
		results.Single(r => r != null)!.Dispose();
		using var replacement = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, clock.GetUtcNow().AddSeconds(20), default);
	}

	[Test]
	public async Task ReservationProtectsEveryAddressWindowAndLateConsumptionStillMakesProgress()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 100, clock.GetUtcNow().AddSeconds(20), default);
		for (var i = 0; i < 5; i++) Assert.That(limiter.TryAcquire(TInvestLimitGroup.Users), Is.EqualTo(TimeSpan.Zero));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.Users), Is.GreaterThan(TimeSpan.Zero));
		clock.Advance(TimeSpan.FromSeconds(5));
		var times = new List<DateTimeOffset>();
		for (var i = 0; i < 100; i++)
		{
			var acquire = reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
			if (!acquire.IsCompleted) clock.Advance(TimeSpan.FromSeconds(1));
			await acquire.WaitAsync(TimeSpan.FromSeconds(5));
			times.Add(clock.GetUtcNow());
		}
		Assert.That(times.All(t => times.Count(x => x > t.AddSeconds(-1) && x <= t) <= 45), Is.True);
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.Zero));
	}

	[Test]
	public async Task CorrectionAfterReservationIsHonoredAndCancellationDoesNotSpendSlot()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 1, clock.GetUtcNow().AddSeconds(20), default);
		limiter.Correct(TInvestLimitGroup.MarketData, null, 0, TimeSpan.FromSeconds(60), exhausted: true);
		Assert.ThrowsAsync<TInvestRequestException>(() => reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default));
		clock.Advance(TimeSpan.FromSeconds(60));
		using var cancel = new CancellationTokenSource();
		cancel.Cancel();
		Assert.ThrowsAsync<OperationCanceledException>(() => reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), cancel.Token));
		await reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
	}

	[Test]
	public async Task SlowSourceKeepsUnusedServiceQuotaBeyondOriginalSchedule()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, clock.GetUtcNow().AddSeconds(20), default);
		clock.Advance(TimeSpan.FromSeconds(70));
		for (var i = 0; i < 140; i++)
		{
			if (i > 0 && i % 45 == 0) clock.Advance(TimeSpan.FromSeconds(1));
			Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.Zero));
		}
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.GreaterThan(TimeSpan.FromSeconds(20)));
		await reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
	}

	[TestCase(1L)]
	[TestCase(10000L)]
	public async Task FractionalDriftDoesNotQueueFirstCallBehindOwnPlanAndRollingWindowsHold(long driftTicks)
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, clock.GetUtcNow().AddSeconds(20), default);
		clock.Advance(TimeSpan.FromTicks(driftTicks));
		var all = new List<DateTimeOffset>();
		var history = new List<DateTimeOffset>();
		for (var i = 0; i < 5; i++)
		{
			Assert.That(limiter.TryAcquire(TInvestLimitGroup.Instruments), Is.EqualTo(TimeSpan.Zero));
			all.Add(clock.GetUtcNow());
		}
		for (var i = 0; i < 400; i++)
		{
			var acquire = reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
			if (i == 0) Assert.That(acquire.IsCompletedSuccessfully, Is.True, "A plan must not wait behind its own future slots after clock drift.");
			if (!acquire.IsCompleted) clock.Advance(TimeSpan.FromSeconds(1));
			await acquire.WaitAsync(TimeSpan.FromSeconds(5));
			history.Add(clock.GetUtcNow());
			all.Add(clock.GetUtcNow());
			clock.Advance(TimeSpan.FromTicks(1));
		}
		Assert.That(history.All(t => history.Count(x => x > t.AddSeconds(-1) && x <= t) <= 40), Is.True);
		Assert.That(all.All(t => all.Count(x => x > t.AddSeconds(-1) && x <= t) <= 45), Is.True);
	}

	[Test]
	public async Task OtherReservationAddressSlotsStillConstrainAdmission()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var first = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 80, clock.GetUtcNow().AddSeconds(20), default);
		using var second = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 80, clock.GetUtcNow().AddSeconds(20), default);
		clock.Advance(TimeSpan.FromMilliseconds(1));
		var firstCall = first.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
		Assert.That(firstCall.IsCompletedSuccessfully, Is.True);
		var secondCall = second.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
		Assert.That(secondCall.IsCompleted, Is.False, "A caller cannot ignore another owner's reserved address slots.");
		clock.Advance(TimeSpan.FromSeconds(2));
		await secondCall.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task RetryUsesHistoryPacingAndLeavesRemainingAddressSlotsForLookups()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 40, clock.GetUtcNow().AddSeconds(20), default);
		clock.Advance(TimeSpan.FromMilliseconds(1));
		for (var i = 0; i < 40; i++) await reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
		var retry = reservation.AcquireRetryAsync(limiter, 41, clock.GetUtcNow().AddSeconds(20), default);
		Assert.That(retry.IsCompleted, Is.False, "A quota retry cannot be the 41st historical call in a rolling second.");
		for (var i = 0; i < 5; i++) Assert.That(limiter.TryAcquire(TInvestLimitGroup.Instruments), Is.EqualTo(TimeSpan.Zero));
		clock.Advance(TimeSpan.FromSeconds(1));
		await retry.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task SubMillisecondQuotaWaitYieldsUntilNextClockTickInsteadOfSpinning()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		for (var i = 0; i < 45; i++) Assert.That(limiter.TryAcquire(TInvestLimitGroup.Users), Is.EqualTo(TimeSpan.Zero));
		clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 1));
		var acquire = limiter.AcquireAsync(TInvestLimitGroup.Instruments, clock.GetUtcNow().AddSeconds(8), default);
		Assert.That(acquire.IsCompleted, Is.False);
		clock.Advance(TimeSpan.FromMilliseconds(1));
		await acquire.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task FullHistoryPlanLeavesLookupHeadroomAndCorrectionsCountActualCallsOnly()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, clock.GetUtcNow().AddSeconds(20), default);
		await limiter.AcquireAsync(TInvestLimitGroup.Instruments, clock.GetUtcNow().AddSeconds(8), default);
		Assert.That(clock.GetUtcNow(), Is.EqualTo(DateTimeOffset.UnixEpoch));
		limiter.Correct(TInvestLimitGroup.MarketData, null, 300, TimeSpan.FromSeconds(60));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.FromSeconds(60)), "Unused reservations must not hide lower upstream quota.");
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.Instruments), Is.EqualTo(TimeSpan.Zero));
	}
}
