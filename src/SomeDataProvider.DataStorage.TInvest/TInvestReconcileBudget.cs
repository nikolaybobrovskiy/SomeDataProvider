namespace SomeDataProvider.DataStorage.TInvest;

/// <summary>One store-owned rolling ledger per stable logical profile, never per socket or generation.</summary>
public sealed class TInvestReconcileBudget
{
	const int MaximumScopes = 1024;
	readonly TimeProvider _clock;
	readonly int _requestsPerMinute;
	readonly object _lock = new();
	readonly Dictionary<string, Ledger> _ledgers = new(StringComparer.Ordinal);

	public TInvestReconcileBudget(TimeProvider clock, int requestsPerMinute)
	{
		ArgumentNullException.ThrowIfNull(clock);
		if (requestsPerMinute is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(requestsPerMinute), "Use 1-12 requests per rolling minute.");
		_clock = clock;
		_requestsPerMinute = requestsPerMinute;
	}

	public Request BeginRequest(string scope, int maximumAttempts)
	{
		if (string.IsNullOrWhiteSpace(scope) || scope.Length > 256 || maximumAttempts is < 0 or > 2)
			throw new ArgumentException("Use a stable profile scope and 0-2 source attempts.");
		lock (_lock)
		{
			foreach (var pair in _ledgers.ToArray())
			{
				Prune(pair.Value.Requests);
				Prune(pair.Value.Dispatches);
				if (!pair.Value.InFlight && pair.Value.Requests.Count == 0 && pair.Value.Dispatches.Count == 0) _ledgers.Remove(pair.Key);
			}
			if (!_ledgers.TryGetValue(scope, out var ledger))
			{
				if (_ledgers.Count >= MaximumScopes) throw Reject("BudgetExhausted");
				_ledgers.Add(scope, ledger = new());
			}
			if (ledger.Requests.Count >= _requestsPerMinute) throw Reject("BudgetExhausted");
			// Refused overlapping/invalid work still spends a request, but never releases another request's slot.
			ledger.Requests.Enqueue(_clock.GetTimestamp());
			if (ledger.InFlight) throw Reject("ReconcileBusy");
			ledger.InFlight = true;
			return new(this, ledger, maximumAttempts);
		}
	}

	public void ValidateWindow(DateTimeOffset from, DateTimeOffset to)
	{
		var now = _clock.GetUtcNow();
		if (from.Ticks % TimeSpan.TicksPerSecond != 0 || to.Ticks % TimeSpan.TicksPerSecond != 0 || from <= DateTimeOffset.UnixEpoch || to <= from)
			throw Reject("InvalidRequest");
		if (from < now.AddMinutes(-58)) throw Reject("RecentWindowExpired");
		if (to > now.AddSeconds(-2)) throw Reject("ClockMismatch");
	}

	internal static TInvestRecentRequestException Reject(string reason) => new(reason);

	void Prune(Queue<long> queue)
	{
		while (queue.TryPeek(out var at) && _clock.GetElapsedTime(at) >= TimeSpan.FromSeconds(60)) queue.Dequeue();
	}

	public sealed class Request : IDisposable
	{
		readonly TInvestReconcileBudget _owner;
		readonly Ledger _ledger;
		readonly int _maximumAttempts;
		int _attempts;
		bool _disposed;

		internal Request(TInvestReconcileBudget owner, Ledger ledger, int maximumAttempts)
		{
			_owner = owner;
			_ledger = ledger;
			_maximumAttempts = maximumAttempts;
		}

		public int Attempts
		{
			get { lock (_owner._lock) return _attempts; }
		}

		/// <summary>Called after shared-source admission, immediately before each actual upstream dispatch, including retries.</summary>
		public void BeforeDispatch(DateTimeOffset from, DateTimeOffset to)
		{
			lock (_owner._lock)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				_owner.ValidateWindow(from, to);
				_owner.Prune(_ledger.Dispatches);
				if (_attempts >= _maximumAttempts || _ledger.Dispatches.Count >= 2 * _owner._requestsPerMinute) throw Reject("BudgetExhausted");
				_attempts++;
				_ledger.Dispatches.Enqueue(_owner._clock.GetTimestamp());
			}
		}

		public void Dispose()
		{
			lock (_owner._lock)
			{
				if (_disposed) return;
				_disposed = true;
				_ledger.InFlight = false;
			}
		}
	}

	internal sealed class Ledger
	{
		public Queue<long> Requests { get; } = new();
		public Queue<long> Dispatches { get; } = new();
		public bool InFlight { get; set; }
	}
}

public sealed class TInvestRecentRequestException(string reason) : Exception("Recent tick request rejected: " + reason + ".")
{
	public string Reason { get; } = reason;
}
