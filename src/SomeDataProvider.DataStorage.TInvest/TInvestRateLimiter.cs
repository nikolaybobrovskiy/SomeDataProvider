namespace SomeDataProvider.DataStorage.TInvest;

public enum TInvestLimitGroup
{
	Instruments,
	MarketData,
	Users,
	Subscriptions,
	Archives,
}

/// <summary>One shared limiter per source. Admission and header corrections are atomic across callers.</summary>
public sealed class TInvestRateLimiter
{
	readonly object _lock = new();
	readonly TimeProvider _clock;
	readonly Window _address = new(45, TimeSpan.FromSeconds(1));
	readonly Dictionary<TInvestLimitGroup, Window> _windows = new()
	{
		[TInvestLimitGroup.Instruments] = new(180, TimeSpan.FromMinutes(1)),
		[TInvestLimitGroup.MarketData] = new(540, TimeSpan.FromMinutes(1)),
		[TInvestLimitGroup.Users] = new(90, TimeSpan.FromMinutes(1)),
		[TInvestLimitGroup.Subscriptions] = new(90, TimeSpan.FromMinutes(1)),
		[TInvestLimitGroup.Archives] = new(27, TimeSpan.FromMinutes(1)),
	};

	public TInvestRateLimiter(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

	public TimeSpan TryAcquire(TInvestLimitGroup group)
	{
		lock (_lock)
		{
			var now = _clock.GetTimestamp();
			var window = _windows[group];
			var delay = Max(Delay(window, now), Delay(_address, now));
			if (delay > TimeSpan.Zero) return delay;
			window.Calls.Enqueue(now);
			_address.Calls.Enqueue(now);
			return TimeSpan.Zero;
		}
	}

	public async Task AcquireAsync(TInvestLimitGroup group, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_clock.GetUtcNow() >= deadline) throw Limited(TimeSpan.FromSeconds(1));
			var delay = TryAcquire(group);
			if (delay == TimeSpan.Zero) return;
			if (_clock.GetUtcNow() + delay >= deadline) throw Limited(delay);
			await Task.Delay(delay, _clock, cancellationToken).ConfigureAwait(false);
		}
	}

	public void Correct(TInvestLimitGroup group, int? limit, int? remaining, TimeSpan resetAfter, bool exhausted = false)
	{
		lock (_lock)
		{
			var window = _windows[group];
			if (limit > 0) window.Capacity = Math.Min(window.Capacity, Math.Max(1, (int)(limit.Value * 0.9)));
			var now = _clock.GetTimestamp();
			Delay(window, now);
			if (exhausted || (remaining.HasValue && remaining.Value < window.Capacity - window.Calls.Count))
			{
				// Never let a late response reopen a window closed by another response.
				var duration = resetAfter > TimeSpan.Zero ? resetAfter : TimeSpan.FromMinutes(1);
				window.BlockedUntil = Math.Max(window.BlockedUntil, now + (long)(duration.TotalSeconds * _clock.TimestampFrequency));
			}
		}
	}

	static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;

	static TInvestRequestException Limited(TimeSpan delay) => new($"T-Invest request limit reached; retry in {Math.Clamp((int)Math.Ceiling(delay.TotalSeconds), 1, 300)} s.", TInvestFailureKind.Limit);

	TimeSpan Delay(Window window, long now)
	{
		while (window.Calls.TryPeek(out var oldest) && _clock.GetElapsedTime(oldest, now) >= window.Duration)
			window.Calls.Dequeue();
		var blocked = window.BlockedUntil > now ? TimeSpan.FromSeconds((double)(window.BlockedUntil - now) / _clock.TimestampFrequency) : TimeSpan.Zero;
		if (window.Calls.Count >= window.Capacity)
			blocked = Max(blocked, window.Duration - _clock.GetElapsedTime(window.Calls.Peek(), now));
		return blocked;
	}

	sealed class Window(int capacity, TimeSpan duration)
	{
		public int Capacity { get; set; } = capacity;
		public TimeSpan Duration { get; } = duration;
		public Queue<long> Calls { get; } = new();
		public long BlockedUntil { get; set; }
	}
}
