namespace SomeDataProvider.DataStorage.TInvest.Test;

using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class RateLimiterTests
{
	[TestCase(TInvestLimitGroup.Instruments, 180)]
	[TestCase(TInvestLimitGroup.MarketData, 540)]
	[TestCase(TInvestLimitGroup.Users, 90)]
	[TestCase(TInvestLimitGroup.Subscriptions, 90)]
	[TestCase(TInvestLimitGroup.Archives, 27)]
	public void RollingWindowNeverRefillsAtMinuteBoundary(TInvestLimitGroup group, int capacity)
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		clock.Advance(TimeSpan.FromSeconds(59));
		for (var i = 0; i < capacity; i++)
		{
			if (i > 0 && i % 45 == 0) clock.Advance(TimeSpan.FromSeconds(1));
			Assert.That(limiter.TryAcquire(group), Is.EqualTo(TimeSpan.Zero));
		}
		Assert.That(limiter.TryAcquire(group), Is.GreaterThan(TimeSpan.Zero));
		clock.Advance(TimeSpan.FromSeconds(60));
		Assert.That(limiter.TryAcquire(group), Is.EqualTo(TimeSpan.Zero));
	}

	[Test]
	public void AddressLimitIsSharedAcrossGroups()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		for (var i = 0; i < 45; i++) Assert.That(limiter.TryAcquire(i % 2 == 0 ? TInvestLimitGroup.Instruments : TInvestLimitGroup.Users), Is.EqualTo(TimeSpan.Zero));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.FromSeconds(1)));
		clock.Advance(TimeSpan.FromSeconds(1));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.Zero));
	}

	[Test]
	public void HeaderCorrectionOnlyClosesAndLateResponsesCannotReopen()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.Instruments), Is.EqualTo(TimeSpan.Zero));
		limiter.Correct(TInvestLimitGroup.Instruments, 100, 2, TimeSpan.FromSeconds(12));
		limiter.Correct(TInvestLimitGroup.Instruments, 200, 199, TimeSpan.FromSeconds(1));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.Instruments), Is.EqualTo(TimeSpan.FromSeconds(12)));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.Zero));
		clock.Advance(TimeSpan.FromSeconds(12));
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.Instruments), Is.EqualTo(TimeSpan.Zero));
	}

	[Test]
	public void ResourceExhaustedUsesSixtySecondFallbackAndRejectsAnEightSecondDeadline()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		limiter.Correct(TInvestLimitGroup.Instruments, null, null, TimeSpan.Zero, exhausted: true);
		Assert.That(limiter.TryAcquire(TInvestLimitGroup.Instruments), Is.EqualTo(TimeSpan.FromSeconds(60)));
		var exception = Assert.ThrowsAsync<TInvestRequestException>(() => limiter.AcquireAsync(TInvestLimitGroup.Instruments, clock.GetUtcNow().AddSeconds(8), default));
		Assert.That(exception!.Message, Does.Contain("retry in 60 s"));
	}

	[Test]
	public async Task FakeTimeAdmissionAndCancellation()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		limiter.Correct(TInvestLimitGroup.Instruments, null, 0, TimeSpan.FromSeconds(2));
		var task = limiter.AcquireAsync(TInvestLimitGroup.Instruments, clock.GetUtcNow().AddSeconds(8), default);
		Assert.That(task.IsCompleted, Is.False);
		clock.Advance(TimeSpan.FromSeconds(2));
		await task.WaitAsync(TimeSpan.FromSeconds(5));
		using var cancelled = new CancellationTokenSource();
		cancelled.Cancel();
		Assert.ThrowsAsync<OperationCanceledException>(() => limiter.AcquireAsync(TInvestLimitGroup.Instruments, clock.GetUtcNow().AddSeconds(8), cancelled.Token));
		Assert.That(clock.ActiveTimers, Is.Zero);
	}
}
