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
		// Unused booked calls keep the history share of the address as well, so other callers get five per second.
		for (var i = 0; i < 140; i++)
		{
			if (i > 0 && i % 5 == 0) clock.Advance(TimeSpan.FromSeconds(1));
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
	public async Task OtherOwnersBookingsStillConstrainEveryCaller()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var first = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 80, clock.GetUtcNow().AddSeconds(20), default);
		using var second = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 80, clock.GetUtcNow().AddSeconds(20), default);
		clock.Advance(TimeSpan.FromMilliseconds(1));
		for (var i = 0; i < 5; i++) Assert.That(limiter.TryAcquire(TInvestLimitGroup.Users), Is.EqualTo(TimeSpan.Zero));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.Users), Is.GreaterThan(TimeSpan.Zero), "Both owners' booked calls hold the history share of the address.");
		var late = Assert.ThrowsAsync<TInvestRequestException>(() => limiter.ReserveAsync(TInvestLimitGroup.MarketData, 300, clock.GetUtcNow().AddSeconds(10), default));
		Assert.That(late!.Kind, Is.EqualTo(TInvestFailureKind.Limit), "A third plan queues behind all 160 booked calls: 460 calls need 11 s at 40 per second.");
		var full = Assert.ThrowsAsync<TInvestRequestException>(() => limiter.ReserveAsync(TInvestLimitGroup.MarketData, 381, clock.GetUtcNow().AddSeconds(20), default));
		Assert.That(full!.Kind, Is.EqualTo(TInvestFailureKind.Limit), "Booked service quota stays held for its owners.");
		using var third = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 300, clock.GetUtcNow().AddSeconds(20), default);

		// Plans share one 40-per-second history lane: neither owner's booked calls stall the other,
		// but together their actual calls never pass 40 in a rolling second.
		for (var i = 0; i < 20; i++)
		{
			Assert.That(first.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default).IsCompletedSuccessfully, Is.True);
			Assert.That(second.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default).IsCompletedSuccessfully, Is.True);
		}
		var waiting = third.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
		Assert.That(waiting.IsCompleted, Is.False, "The 41st history call in a rolling second waits.");
		clock.Advance(TimeSpan.FromSeconds(1));
		await waiting.WaitAsync(TimeSpan.FromSeconds(5));
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

	[Test]
	public async Task LookupAfterDriftedFortyCallBatchIsAdmittedAtOnce()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, clock.GetUtcNow().AddSeconds(20), default);
		clock.Advance(TimeSpan.FromMilliseconds(1));
		for (var i = 0; i < 40; i++) Assert.That(reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default).IsCompletedSuccessfully, Is.True);
		clock.Advance(TimeSpan.FromMilliseconds(499));
		var lookup = limiter.AcquireAsync(TInvestLimitGroup.Instruments, clock.GetUtcNow().AddSeconds(8), default);
		Assert.That(lookup.IsCompletedSuccessfully, Is.True, "Forty actual history calls leave a lookup its five slots; the plan's next calls are not counted twice.");
	}

	[TestCase(80, 1L)]
	[TestCase(80, 10000L)]
	[TestCase(400, 1L)]
	[TestCase(400, 10000L)]
	public async Task DriftedBatchLeavesFiveImmediateControlSlots(int calls, long driftTicks)
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, calls, clock.GetUtcNow().AddSeconds(20), default);
		clock.Advance(TimeSpan.FromTicks(driftTicks));
		for (var i = 0; i < 40; i++) Assert.That(reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default).IsCompletedSuccessfully, Is.True);
		for (var i = 0; i < 5; i++) Assert.That(limiter.TryAcquire(TInvestLimitGroup.Instruments), Is.EqualTo(TimeSpan.Zero));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.Instruments), Is.GreaterThan(TimeSpan.Zero).And.LessThanOrEqualTo(TimeSpan.FromSeconds(1)));
	}

	[Test]
	public async Task FirstCallIsAdmittedAtOnceForEveryPlanSize([Values(1, 40, 79, 80, 120, 366, 400)] int calls, [Values(0L, 1L, 10000L)] long driftTicks)
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, calls, clock.GetUtcNow().AddSeconds(20), default);
		clock.Advance(TimeSpan.FromTicks(driftTicks));
		Assert.That(reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default).IsCompletedSuccessfully, Is.True);
	}

	[Test]
	public async Task HistoryAndControlCallsShareFortyAndFortyFiveCaps()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, clock.GetUtcNow().AddSeconds(20), default);
		for (var i = 0; i < 40; i++) Assert.That(reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default).IsCompletedSuccessfully, Is.True);
		var next = reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
		Assert.That(next.IsCompleted, Is.False, "History calls stop at 40 per rolling second.");
		for (var i = 0; i < 5; i++) Assert.That(limiter.TryAcquire(TInvestLimitGroup.Users), Is.EqualTo(TimeSpan.Zero));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.Users), Is.EqualTo(TimeSpan.FromSeconds(1)), "All calls stop at 45 per rolling second.");
		clock.Advance(TimeSpan.FromSeconds(1));
		await next.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task DisposedPlanLeavesNoServiceOrAddressSlots()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, clock.GetUtcNow().AddSeconds(20), default);
		for (var i = 0; i < 10; i++) await reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
		reservation.Dispose();
		Assert.ThrowsAsync<ObjectDisposedException>(() => reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default));
		for (var i = 0; i < 35; i++) Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.Zero));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.FromSeconds(1)));
		clock.Advance(TimeSpan.FromSeconds(1));
		using var replacement = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 495, clock.GetUtcNow().AddSeconds(20), default);
	}

	[Test]
	public async Task HeaderCorrectionCountsConsumedCallsOnce()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 100, clock.GetUtcNow().AddSeconds(20), default);
		clock.Advance(TimeSpan.FromMilliseconds(1));
		for (var i = 0; i < 40; i++) await reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
		limiter.Correct(TInvestLimitGroup.MarketData, 600, 540 - 40, TimeSpan.FromSeconds(30));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.Zero), "Forty used calls match a header reporting 500 remaining.");
		limiter.Correct(TInvestLimitGroup.MarketData, 600, 540 - 41 - 1, TimeSpan.FromSeconds(30));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.FromSeconds(30)));
	}

	[Test]
	public async Task RetryCountsOnceAndLeavesControlSlots()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 80, clock.GetUtcNow().AddSeconds(20), default);
		clock.Advance(TimeSpan.FromMilliseconds(1));
		for (var i = 0; i < 39; i++) await reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
		Assert.That(reservation.AcquireRetryAsync(limiter, 400, clock.GetUtcNow().AddSeconds(20), default).IsCompletedSuccessfully, Is.True);
		for (var i = 0; i < 5; i++) Assert.That(limiter.TryAcquire(TInvestLimitGroup.Instruments), Is.EqualTo(TimeSpan.Zero));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.Instruments), Is.GreaterThan(TimeSpan.Zero));
	}

	[Test]
	public void ReservationRejectsAnotherLimiter()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = limiter.ReserveAsync(TInvestLimitGroup.MarketData, 2, clock.GetUtcNow().AddSeconds(20), default).Result;
		var other = new TInvestRateLimiter(clock);
		Assert.Throws<ArgumentException>(() => reservation.AcquireAsync(other, clock.GetUtcNow().AddSeconds(20), default));
		Assert.ThrowsAsync<ArgumentException>(() => reservation.AcquireRetryAsync(other, 400, clock.GetUtcNow().AddSeconds(20), default));
	}

	[Test]
	public async Task GatewayLookupDuringDriftedCandleDownloadIsAdmittedAtOnce()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var gateway = new SdkTInvestGateway(new CandleAndSearchInvoker(), new TInvestOptions(), limiter, clock);
		using var reservation = await gateway.ReserveCandlesAsync(400, clock.GetUtcNow().AddSeconds(20), default);
		clock.Advance(TimeSpan.FromMilliseconds(1));
		var request = new TInvestCandleRequest("invented-uid", clock.GetUtcNow().AddHours(-1), clock.GetUtcNow(), 60, 2400, false);
		for (var i = 0; i < 40; i++) Assert.That(gateway.GetCandlesAsync(request, reservation, clock.GetUtcNow().AddSeconds(20), default).IsCompletedSuccessfully, Is.True);
		clock.Advance(TimeSpan.FromMilliseconds(499));
		Assert.That(gateway.FindInstrumentsAsync("SYNTH", clock.GetUtcNow().AddSeconds(8), default).IsCompletedSuccessfully, Is.True);
	}

	[Test]
	public async Task ReservationAfterQuotaBlockIsAdmittedOnlyIfItsPacedCallsFitTheDeadline()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		limiter.Correct(TInvestLimitGroup.MarketData, null, null, TimeSpan.FromSeconds(19), exhausted: true);
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, clock.GetUtcNow().AddSeconds(20), default));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit), "400 calls from +19 s need nine more seconds at 40 per second.");
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 40, clock.GetUtcNow().AddSeconds(20), default);
	}

	[Test]
	public async Task ReservationAfterQuotaBlockQueuesBehindOtherPlansBookedCalls()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var first = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 300, clock.GetUtcNow().AddSeconds(20), default);
		limiter.Correct(TInvestLimitGroup.MarketData, null, null, TimeSpan.FromSeconds(10), exhausted: true);

		// The first plan's 300 calls also wait for the block, so from 10 s the 440th booked call goes at 20 s.
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => limiter.ReserveAsync(TInvestLimitGroup.MarketData, 140, clock.GetUtcNow().AddSeconds(20), default));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		using var second = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 100, clock.GetUtcNow().AddSeconds(20), default);
	}

	[Test]
	public async Task EmptyReservationIsNotHeldByOtherPlansBookedCalls()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var first = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, clock.GetUtcNow().AddSeconds(20), default);
		limiter.Correct(TInvestLimitGroup.MarketData, null, null, TimeSpan.FromSeconds(19), exhausted: true);

		// A range before the instrument's first candle plans no calls and needs no quota.
		using var empty = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 0, clock.GetUtcNow().AddSeconds(20), default);
		var late = Assert.ThrowsAsync<TInvestRequestException>(() => limiter.ReserveAsync(TInvestLimitGroup.MarketData, 0, clock.GetUtcNow(), default));
		Assert.That(late!.Kind, Is.EqualTo(TInvestFailureKind.Limit), "A deadline already reached still rejects.");
	}

	[Test]
	public async Task ReservationBehindAFullServiceWindowKeepsFortyPerSecond()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		for (var i = 0; i < 540; i++)
		{
			if (i > 0 && i % 45 == 0) clock.Advance(TimeSpan.FromSeconds(1));
			Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.Zero));
		}

		// At 49 s the window frees 45 calls a second from 60 s, but history calls go at most 40 a second.
		clock.Advance(TimeSpan.FromSeconds(38));
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, clock.GetUtcNow().AddSeconds(20), default));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit), "The 400th call could go at 69 s, the deadline.");
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 360, clock.GetUtcNow().AddSeconds(20), default);
	}

	[TestCase(300)]
	[TestCase(444)]
	public async Task LowerLimitHeaderDoesNotHoldAPlanBehindItsOwnBookings(int limit)
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 400, clock.GetUtcNow().AddSeconds(20), default);
		await reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
		var capacity = (int)(limit * 0.9);
		limiter.Correct(TInvestLimitGroup.MarketData, limit, capacity - 1, TimeSpan.FromSeconds(60));
		Assert.That(reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default).IsCompletedSuccessfully, Is.True, "The plan's own bookings do not hold it back.");
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.GreaterThan(TimeSpan.Zero), "Other callers still count the plan's bookings.");
		for (var sent = 2; sent < capacity; sent++)
		{
			var call = reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
			if (!call.IsCompleted) clock.Advance(TimeSpan.FromSeconds(1));
			await call.WaitAsync(TimeSpan.FromSeconds(5));
		}
		var full = Assert.ThrowsAsync<TInvestRequestException>(() => reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default));
		Assert.That(full!.Kind, Is.EqualTo(TInvestFailureKind.Limit), "Actual calls still stop at the lowered capacity.");
	}

	[Test]
	public async Task LowerLimitHeaderDoesNotHoldARetryBehindItsOwnBookings()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var reservation = await limiter.ReserveAsync(TInvestLimitGroup.MarketData, 100, clock.GetUtcNow().AddSeconds(20), default);
		await reservation.AcquireAsync(limiter, clock.GetUtcNow().AddSeconds(20), default);
		limiter.Correct(TInvestLimitGroup.MarketData, 100, 89, TimeSpan.FromSeconds(60));
		Assert.That(reservation.AcquireRetryAsync(limiter, 400, clock.GetUtcNow().AddSeconds(20), default).IsCompletedSuccessfully, Is.True);
	}

	sealed class CandleAndSearchInvoker : Grpc.Core.CallInvoker
	{
		public override Grpc.Core.AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Grpc.Core.Method<TRequest, TResponse> method, string? host, Grpc.Core.CallOptions options, TRequest request)
		{
			object response = method.Name == "FindInstrument" ? new Tinkoff.InvestApi.V1.FindInstrumentResponse() : new Tinkoff.InvestApi.V1.GetCandlesResponse();
			return new(Task.FromResult((TResponse)response), Task.FromResult(new Grpc.Core.Metadata()), () => Grpc.Core.Status.DefaultSuccess, () => new Grpc.Core.Metadata(), () => { });
		}

		public override TResponse BlockingUnaryCall<TRequest, TResponse>(Grpc.Core.Method<TRequest, TResponse> method, string? host, Grpc.Core.CallOptions options, TRequest request) => throw new NotSupportedException();
		public override Grpc.Core.AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Grpc.Core.Method<TRequest, TResponse> method, string? host, Grpc.Core.CallOptions options, TRequest request) => throw new NotSupportedException();
		public override Grpc.Core.AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Grpc.Core.Method<TRequest, TResponse> method, string? host, Grpc.Core.CallOptions options) => throw new NotSupportedException();
		public override Grpc.Core.AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Grpc.Core.Method<TRequest, TResponse> method, string? host, Grpc.Core.CallOptions options) => throw new NotSupportedException();
	}
}
