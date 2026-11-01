namespace SomeDataProvider.DataStorage.TInvest;

public enum TInvestLimitGroup
{
	Instruments,
	MarketData,
	Users,
	Subscriptions,
	Archives,
}

/// <summary>Shared rolling windows, including future slots reserved by history plans.</summary>
public sealed class TInvestRateLimiter
{
	readonly object _lock = new();
	readonly TimeProvider _clock;
	readonly Window _address = new(45, TimeSpan.FromSeconds(1));
	// Leave at least five address slots per second outside history reservations for lookups/control.
	readonly Window _historyAddress = new(40, TimeSpan.FromSeconds(1));
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
			var at = Available(group, now);
			if (at > now) return Elapsed(now, at);
			Add(group, new Slot(now, false));
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
			await WaitAsync(delay, deadline, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Reserves every service and address slot atomically; a failed plan leaves no slots behind.</summary>
	public Task<Reservation> ReserveAsync(TInvestLimitGroup group, int count, DateTimeOffset deadline, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		lock (_lock)
		{
			if (count < 0 || count > _windows[group].Capacity) throw Limited(TimeSpan.FromMinutes(1));
			var now = _clock.GetTimestamp();
			var slots = new List<Slot>();
			var addressOwner = new object();
			try
			{
				var at = now;
				for (var i = 0; i < count; i++)
				{
					at = Available(group, at, history: true);
					var slot = new Slot(at, true, addressOwner);
					slots.Add(slot);
					Add(group, slot);
				}
				var delay = Elapsed(now, at);
				if (_clock.GetUtcNow() + delay >= deadline) throw Limited(delay);
				return Task.FromResult(new Reservation(this, group, slots, addressOwner));
			}
			catch
			{
				foreach (var slot in slots) Remove(group, slot);
				throw;
			}
		}
	}

	public void Correct(TInvestLimitGroup group, int? limit, int? remaining, TimeSpan resetAfter, bool exhausted = false)
	{
		lock (_lock)
		{
			var window = _windows[group];
			if (limit > 0) window.Capacity = Math.Min(window.Capacity, Math.Max(1, (int)(limit.Value * 0.9)));
			var now = _clock.GetTimestamp();
			Prune(window, now);
			if (exhausted || (remaining.HasValue && remaining.Value < window.Capacity - window.Calls.Count(s => !s.Reserved)))
			{
				var duration = resetAfter > TimeSpan.Zero ? resetAfter : TimeSpan.FromMinutes(1);
				window.BlockedUntil = Math.Max(window.BlockedUntil, now + Ticks(duration));
			}
		}
	}

	static TInvestRequestException Limited(TimeSpan delay) => new($"T-Invest request limit reached; retry in {Math.Clamp((int)Math.Ceiling(delay.TotalSeconds), 1, 300)} s.", TInvestFailureKind.Limit);

	async Task WaitAsync(TimeSpan delay, DateTimeOffset deadline, CancellationToken ct)
	{
		// Task.Delay uses whole milliseconds. Round up so sub-millisecond quota waits
		// cannot turn into a synchronous zero-delay loop on a manually advanced clock.
		delay = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(delay.TotalMilliseconds)));
		if (_clock.GetUtcNow() + delay >= deadline) throw Limited(delay);
		await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
	}

	async Task ConsumeAsync(Reservation reservation, DateTimeOffset deadline, CancellationToken ct)
	{
		while (true)
		{
			ct.ThrowIfCancellationRequested();
			TimeSpan delay;
			lock (_lock)
			{
				ObjectDisposedException.ThrowIf(reservation.Disposed, reservation);
				if (reservation.Slots.Count == 0) throw new TInvestRequestException("T-Invest candle plan exhausted.");
				var slot = reservation.Slots[0];
				Remove(reservation.Group, slot);
				var now = _clock.GetTimestamp();
				var at = Available(reservation.Group, Math.Max(now, slot.At), history: true, reservation.AddressOwner);
				delay = Elapsed(now, at);
				slot.At = at;
				Add(reservation.Group, slot);
				if (delay <= TimeSpan.Zero && _clock.GetUtcNow() < deadline)
				{
					slot.Reserved = false;
					reservation.Slots.RemoveAt(0);
					reservation.Attempts++;
					return;
				}
			}
			await WaitAsync(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), deadline, ct).ConfigureAwait(false);
		}
	}

	async Task AcquireHistoryRetryAsync(Reservation reservation, DateTimeOffset deadline, CancellationToken ct)
	{
		while (true)
		{
			ct.ThrowIfCancellationRequested();
			TimeSpan delay;
			lock (_lock)
			{
				ObjectDisposedException.ThrowIf(reservation.Disposed, reservation);
				if (_clock.GetUtcNow() >= deadline) throw Limited(TimeSpan.FromSeconds(1));
				var now = _clock.GetTimestamp();
				var at = Available(reservation.Group, now, history: true, reservation.AddressOwner);
				delay = Elapsed(now, at);
				if (delay <= TimeSpan.Zero)
				{
					Add(reservation.Group, new Slot(now, false, reservation.AddressOwner), history: true);
					return;
				}
			}
			await WaitAsync(delay, deadline, ct).ConfigureAwait(false);
		}
	}

	long Available(TInvestLimitGroup group, long at, bool history = false, object? addressOwner = null)
	{
		var now = _clock.GetTimestamp();
		var window = _windows[group];
		Prune(window, now);
		Prune(_address, now);
		Prune(_historyAddress, now);
		while (true)
		{
			var next = Math.Max(Available(window, at, now), Available(_address, at, now, addressOwner));
			if (history) next = Math.Max(next, Available(_historyAddress, at, now, addressOwner));
			if (next == at) return at;
			at = next;
		}
	}

	long Available(Window window, long at, long now, object? addressOwner = null)
	{
		at = Math.Max(at, window.BlockedUntil);
		var duration = Ticks(window.Duration);
		while (true)
		{
			// A new slot must fit around future reservations as well as past admissions.
			// Service capacity stays held until use/disposal even if the source runs slowly.
			// Address reservations are scheduled slots; late calls re-enter that rolling window.
			// A caller reuses its own reserved address capacity. Its unused future slots must not
			// push it behind itself when the real clock advances between reservation and use.
			// Actual admissions and every other owner's reservations still constrain the call.
			var times = window.Calls.Where(s => addressOwner == null || !s.Reserved || !ReferenceEquals(s.AddressOwner, addressOwner))
				.Select(s => s.Reserved && window != _address && window != _historyAddress ? Math.Max(now, s.At) : s.At).Append(at).Order().ToArray();
			var next = at;
			for (var i = 0; i + window.Capacity < times.Length; i++)
			{
				var last = times[i + window.Capacity];
				if (last - times[i] >= duration || at < times[i] || at > last) continue;
				next = Math.Max(next, times[i] + duration);
			}
			if (next == at) return at;
			at = next;
		}
	}

	void Prune(Window window, long now) => window.Calls.RemoveAll(s => !s.Reserved && now - s.At >= Ticks(window.Duration));
	void Add(TInvestLimitGroup group, Slot slot, bool history = false)
	{
		_windows[group].Calls.Add(slot);
		_address.Calls.Add(slot);
		if (slot.Reserved || history) _historyAddress.Calls.Add(slot);
	}

	void Remove(TInvestLimitGroup group, Slot slot)
	{
		_windows[group].Calls.Remove(slot);
		_address.Calls.Remove(slot);
		_historyAddress.Calls.Remove(slot);
	}
	long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _clock.TimestampFrequency);
	TimeSpan Elapsed(long from, long to) => TimeSpan.FromSeconds((double)(to - from) / _clock.TimestampFrequency);

	public sealed class Reservation : IDisposable
	{
		readonly TInvestRateLimiter _owner;
		internal Reservation(TInvestRateLimiter owner, TInvestLimitGroup group, List<Slot> slots, object addressOwner)
		{
			_owner = owner;
			Group = group;
			Slots = slots;
			AddressOwner = addressOwner;
		}

		internal TInvestLimitGroup Group { get; }
		internal List<Slot> Slots { get; }
		internal bool Disposed { get; set; }
		internal int Attempts { get; set; }
		internal object AddressOwner { get; }

		public Task AcquireAsync(TInvestRateLimiter owner, DateTimeOffset deadline, CancellationToken ct)
		{
			if (!ReferenceEquals(owner, _owner)) throw new ArgumentException("Reservation belongs to another limiter.");
			return _owner.ConsumeAsync(this, deadline, ct);
		}

		public async Task AcquireRetryAsync(TInvestRateLimiter owner, int maximumCalls, DateTimeOffset deadline, CancellationToken ct)
		{
			if (!ReferenceEquals(owner, _owner)) throw new ArgumentException("Reservation belongs to another limiter.");
			lock (_owner._lock)
			{
				ObjectDisposedException.ThrowIf(Disposed, this);
				// Keep room for every unconsumed planned call as well as this retry.
				if (Attempts + Slots.Count >= maximumCalls) throw new TInvestRequestException("T-Invest candle call cap reached; retry in 60 s.", TInvestFailureKind.Limit);
				Attempts++;
			}
			try { await owner.AcquireHistoryRetryAsync(this, deadline, ct).ConfigureAwait(false); }
			catch
			{
				lock (_owner._lock) Attempts--;
				throw;
			}
		}

		public void Dispose()
		{
			lock (_owner._lock)
			{
				if (Disposed) return;
				Disposed = true;
				foreach (var slot in Slots) _owner.Remove(Group, slot);
				Slots.Clear();
			}
		}
	}

	internal sealed class Slot(long at, bool reserved, object? addressOwner = null)
	{
		public long At { get; set; } = at;
		public bool Reserved { get; set; } = reserved;
		public object? AddressOwner { get; } = addressOwner;
	}

	sealed class Window(int capacity, TimeSpan duration)
	{
		public int Capacity { get; set; } = capacity;
		public TimeSpan Duration { get; } = duration;
		public List<Slot> Calls { get; } = new();
		public long BlockedUntil { get; set; }
	}
}
