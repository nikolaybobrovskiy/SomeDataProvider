namespace SomeDataProvider.DataStorage.TInvest;

public enum TInvestLimitGroup
{
	Instruments,
	MarketData,
	Users,
	Subscriptions,
	Archives,
}

/// <summary>
/// Shared rolling windows. A history plan books every call before its first one. Each booked call holds its
/// service quota until it is used or released, and counts once in the address windows: as a booked call until it
/// is sent, then as the actual call.
/// </summary>
public sealed class TInvestRateLimiter
{
	readonly object _lock = new();
	readonly TimeProvider _clock;

	// Both address windows hold actual calls only. Booked history calls are laid out in one lane when a caller
	// other than a history plan is checked (see Lane), so history leaves five of the 45 slots for lookups.
	readonly Window _address = new(45, TimeSpan.FromSeconds(1));
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

	public TimeSpan TryAcquire(TInvestLimitGroup group) => Admit(group, true);

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

	/// <summary>
	/// Books every call of a history plan atomically; a failed plan leaves no slots behind. The plan queues behind
	/// every call already booked, all of them laid out in order after any quota block, within their service windows
	/// and at 40 calls per second, so it is admitted only if its own last call fits the deadline.
	/// </summary>
	public Task<Reservation> ReserveAsync(TInvestLimitGroup group, int count, DateTimeOffset deadline, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		lock (_lock)
		{
			if (count < 0 || count > _windows[group].Capacity) throw Limited(TimeSpan.FromMinutes(1));
			var now = _clock.GetTimestamp();
			PruneAll(now);
			var (history, address) = Actual();
			var service = new Dictionary<Window, List<long>>();
			var at = now;
			foreach (var booked in _windows.Values.SelectMany(w => w.Calls.Where(s => s.Reserved).Select(s => (Window: w, s.At))).OrderBy(b => b.At).ToArray())
				at = Book(history, address, service, booked.Window, at);
			var window = _windows[group];
			var owner = new object();
			var slots = new List<Slot>();
			var last = now;
			try
			{
				for (var i = 0; i < count; i++)
				{
					// Lay the call out behind every booked call, then fit it in the service window as others see it. A
					// later time only relaxes the pacing, so the call stays paced for the calls booked after it.
					at = Available(window, Book(history, address, service, window, at), now);
					history[^1] = at;
					address[^1] = at;
					service[window][^1] = at;
					last = at;
					var slot = new Slot(at, true, owner);
					slots.Add(slot);
					window.Calls.Add(slot);
				}

				// Only the plan's own last call counts: a plan without calls is never held by other plans' bookings.
				var delay = Elapsed(now, last);
				if (_clock.GetUtcNow() + delay >= deadline) throw Limited(delay);
				return Task.FromResult(new Reservation(this, group, slots, owner));
			}
			catch
			{
				foreach (var slot in slots) window.Calls.Remove(slot);
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

	/// <summary>Returns the wait <see cref="TryAcquire"/> would report now without admitting a call; it only drops expired calls.</summary>
	internal TimeSpan Probe(TInvestLimitGroup group) => Admit(group, false);

	static TInvestRequestException Limited(TimeSpan delay) => new($"T-Invest request limit reached; retry in {Math.Clamp((int)Math.Ceiling(delay.TotalSeconds), 1, 300)} s.", TInvestFailureKind.Limit);

	TimeSpan Admit(TInvestLimitGroup group, bool record)
	{
		lock (_lock)
		{
			var now = _clock.GetTimestamp();
			var at = Available(group, now);
			if (at > now) return Elapsed(now, at);
			if (record) Add(group, new Slot(now, false));
			return TimeSpan.Zero;
		}
	}

	async Task WaitAsync(TimeSpan delay, DateTimeOffset deadline, CancellationToken ct)
	{
		var now = _clock.GetUtcNow();
		if (now + delay >= deadline) throw Limited(delay);

		// Wait whole milliseconds where the deadline allows, as the system clock's timers do, but do not round a
		// wait that fits the deadline past it.
		var rounded = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(delay.TotalMilliseconds)));
		await DelayAsync(now + rounded < deadline ? rounded : delay, ct).ConfigureAwait(false);
	}

	async Task DelayAsync(TimeSpan delay, CancellationToken ct)
	{
		// Task.Delay truncates to whole milliseconds, so a sub-millisecond wait would finish at once and spin on a
		// manual clock. A timer from the clock keeps the exact wait.
		var elapsed = new TaskCompletionSource();
		using var timer = _clock.CreateTimer(static state => ((TaskCompletionSource)state!).TrySetResult(), elapsed, delay, Timeout.InfiniteTimeSpan);
		using var registration = ct.Register(() => elapsed.TrySetCanceled(ct));
		await elapsed.Task.ConfigureAwait(false);
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
				if (_clock.GetUtcNow() >= deadline) throw Limited(TimeSpan.FromSeconds(1));
				var slot = reservation.Slots[0];
				var window = _windows[reservation.Group];
				window.Calls.Remove(slot);
				var now = _clock.GetTimestamp();
				var at = Available(reservation.Group, now, reservation.Owner);
				if (at == now)
				{
					// The booked call becomes the actual call in every window; it never counts as both.
					slot.At = now;
					slot.Reserved = false;
					reservation.Slots.RemoveAt(0);
					reservation.Attempts++;
					Add(reservation.Group, slot, history: true);
					return;
				}
				window.Calls.Add(slot);
				delay = Elapsed(now, at);
			}
			await WaitAsync(delay, deadline, ct).ConfigureAwait(false);
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
				var at = Available(reservation.Group, now, reservation.Owner);
				if (at == now)
				{
					Add(reservation.Group, new Slot(now, false, reservation.Owner), history: true);
					return;
				}
				delay = Elapsed(now, at);
			}
			await WaitAsync(delay, deadline, ct).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Finds the earliest time from <paramref name="at"/> when a call fits. A history call (a plan's booked call or
	/// its quota retry, with its plan as <paramref name="history"/>) is checked against actual calls in the address, so
	/// no plan waits behind booked calls, and together history calls stay at 40 per rolling second. In the service
	/// window it counts every actual call and other plans' booked calls, but not its own plan's: the plan never waits
	/// behind its own bookings, also after a header lowers the window's capacity. Every other caller also counts the
	/// booked calls of all plans in both.
	/// </summary>
	long Available(TInvestLimitGroup group, long at, object? history = null)
	{
		var now = _clock.GetTimestamp();
		var window = _windows[group];
		PruneAll(now);
		var lane = history != null ? null : Lane(now, Booked());
		while (true)
		{
			var next = Math.Max(Available(window, at, now, owner: history), Available(_address, at, now, lane));
			if (history != null) next = Math.Max(next, Available(_historyAddress, at, now));
			if (next == at) return at;
			at = next;
		}
	}

	long Available(Window window, long at, long now, IReadOnlyList<long>? lane = null, object? owner = null)
	{
		at = Math.Max(at, window.BlockedUntil);
		var duration = Ticks(window.Duration);
		while (true)
		{
			// A booked service slot counts from its booked time, or from now once that has passed: service quota stays
			// held until the call is used or released, even if the source runs slowly. Only entries within one window
			// of the candidate can share a rolling window with it.
			var candidate = at;
			var times = window.Calls.Where(s => owner == null || !s.Reserved || !ReferenceEquals(s.Owner, owner))
				.Select(s => s.Reserved ? Math.Max(now, s.At) : s.At).Concat(lane ?? Enumerable.Empty<long>())
				.Where(t => t > candidate - duration && t < candidate + duration).Append(at).Order().ToArray();
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

	/// <summary>
	/// Lays out <paramref name="count"/> booked history calls from now, after every actual call, at the earliest times
	/// that keep history calls at 40 and all calls at 45 in every rolling second. The lane follows the plans' actual
	/// progress: a call sent late leaves the lane and appears once as an actual call, and the calls still booked move
	/// later with it, so as others see it booked plus actual history calls never exceed 40 in a rolling second.
	/// </summary>
	List<long> Lane(long now, int count)
	{
		var (history, address) = Actual();
		var lane = new List<long>(count);
		var at = now;
		for (var i = 0; i < count; i++)
		{
			at = Book(history, address, at);
			lane.Add(at);
		}
		return lane;
	}

	(List<long> History, List<long> Address) Actual() =>
		(_historyAddress.Calls.Select(s => s.At).Order().ToList(), _address.Calls.Select(s => s.At).Order().ToList());

	/// <summary>
	/// Appends the next booked call of <paramref name="window"/> at the earliest time from <paramref name="at"/> that its
	/// quota block, its service window and the address pacing allow, counting actual calls and the calls booked before it.
	/// </summary>
	long Book(List<long> history, List<long> address, Dictionary<Window, List<long>> service, Window window, long at)
	{
		if (!service.TryGetValue(window, out var times))
		{
			times = window.Calls.Where(s => !s.Reserved).Select(s => s.At).Order().ToList();
			service[window] = times;
		}
		at = Math.Max(at, window.BlockedUntil);
		if (times.Count >= window.Capacity) at = Math.Max(at, times[times.Count - window.Capacity] + Ticks(window.Duration));
		at = Book(history, address, at);
		times.Add(at);
		return at;
	}

	/// <summary>Appends the next booked call at the earliest time from <paramref name="at"/> that the address pacing allows.</summary>
	long Book(List<long> history, List<long> address, long at)
	{
		// Actual calls are never later than now and booked calls only move later, so each booked call is the newest
		// entry: only the rolling second ending at it can overflow.
		var duration = Ticks(_address.Duration);
		if (history.Count >= _historyAddress.Capacity) at = Math.Max(at, history[history.Count - _historyAddress.Capacity] + duration);
		if (address.Count >= _address.Capacity) at = Math.Max(at, address[address.Count - _address.Capacity] + duration);
		history.Add(at);
		address.Add(at);
		return at;
	}

	int Booked() => _windows.Values.Sum(w => w.Calls.Count(s => s.Reserved));

	void PruneAll(long now)
	{
		foreach (var window in _windows.Values) Prune(window, now);
		Prune(_address, now);
		Prune(_historyAddress, now);
	}

	void Prune(Window window, long now) => window.Calls.RemoveAll(s => !s.Reserved && now - s.At >= Ticks(window.Duration));

	void Add(TInvestLimitGroup group, Slot slot, bool history = false)
	{
		_windows[group].Calls.Add(slot);
		_address.Calls.Add(slot);
		if (history) _historyAddress.Calls.Add(slot);
	}

	long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _clock.TimestampFrequency);

	TimeSpan Elapsed(long from, long to) => TimeSpan.FromSeconds((double)(to - from) / _clock.TimestampFrequency);

	public sealed class Reservation : IDisposable
	{
		readonly TInvestRateLimiter _owner;

		internal Reservation(TInvestRateLimiter owner, TInvestLimitGroup group, List<Slot> slots, object plan)
		{
			_owner = owner;
			Group = group;
			Slots = slots;
			Owner = plan;
		}

		internal TInvestLimitGroup Group { get; }
		internal List<Slot> Slots { get; }
		internal bool Disposed { get; set; }
		internal int Attempts { get; set; }

		/// <summary>Identifies this plan's booked slots, which its own calls do not wait behind.</summary>
		internal object Owner { get; }

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
				foreach (var slot in Slots) _owner._windows[Group].Calls.Remove(slot);
				Slots.Clear();
			}
		}
	}

	internal sealed class Slot(long at, bool reserved, object? owner = null)
	{
		public long At { get; set; } = at;
		public bool Reserved { get; set; } = reserved;
		public object? Owner { get; } = owner;
	}

	sealed class Window(int capacity, TimeSpan duration)
	{
		public int Capacity { get; set; } = capacity;
		public TimeSpan Duration { get; } = duration;
		public List<Slot> Calls { get; } = new();
		public long BlockedUntil { get; set; }
	}
}
