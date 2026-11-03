namespace SomeDataProvider.DataStorage.TInvest;

using System.Globalization;
using SomeDataProvider.DataStorage.Definitions;

public sealed partial class TInvestStore
{
	const int FetchConcurrency = 4;
	const int BackgroundConcurrency = 2;
	const int BackgroundFillMinutes = 15;
	const int ArchivesPerMinute = 27;

	readonly object _tickLock = new();
	readonly Dictionary<(string Uid, DateOnly Day), Task<DayState>> _fetches = new();
	readonly HashSet<(string Uid, DateOnly Day)> _provenEmpty = new();
	readonly Dictionary<(string Path, long Length, bool Dealer, long From, long To), Task<int>> _countTasks = new();
	readonly List<Task> _fills = new();
	readonly SemaphoreSlim _fetchGate = new(FetchConcurrency);
	readonly SemaphoreSlim _backgroundGate = new(BackgroundConcurrency);
	ITickRecording? _recording;
	TradeArchiveCache? _archiveCache;

	enum DayKind
	{
		File,
		Empty,
		Missing,
	}

	public TradeArchiveCache ArchiveCache
	{
		get
		{
			lock (_tickLock) return _archiveCache ??= new(_options.EffectiveArchiveCacheFolder, _options.ArchiveCacheMaxBytes, _clock);
		}
	}

	/// <summary>Completes when every background archive fill started so far has finished. For tests and shutdown.</summary>
	internal Task BackgroundFills
	{
		get
		{
			lock (_tickLock) return Task.WhenAll(_fills.ToArray());
		}
	}

	/// <summary>Test seam: runs just before a day's archive file is loaded for streaming, after the header went out.</summary>
	internal Action<string>? BeforeArchiveLoad { get; set; }

	/// <summary>Test seam: runs on the worker thread just before an archive is counted.</summary>
	internal Action<string>? BeforeArchiveCount { get; set; }

	public void UseTickRecording(ITickRecording recording) => Volatile.Write(ref _recording, recording);

	/// <summary>
	/// Plans and prepares a tick answer: [start, end) in whole UTC seconds, an open end meaning "now". Everything that can
	/// reject the request (caps, missing archives, uncovered recorded spans, the budget) happens here, before any header.
	/// </summary>
	public async Task<TInvestTickReader> CreateTickReaderAsync(TInvestSymbol symbol, DateTime start, DateTime end, DateTimeOffset deadline, CancellationToken ct = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		var now = _clock.GetUtcNow();
		static DateTimeOffset Bound(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
		if (start == default) throw new TInvestRequestException("T-Invest ticks need a start time.");
		if (start.Ticks % TimeSpan.TicksPerSecond != 0 || (end != default && end.Ticks % TimeSpan.TicksPerSecond != 0))
			throw new TInvestRequestException("T-Invest history bounds must be whole UTC seconds.");
		var from = Bound(start);
		var openEnd = end == default || Bound(end) > now;
		var to = openEnd ? now : Bound(end);
		if (to <= from) throw new TInvestRequestException("T-Invest history end must be after start.");
		var firstDay = DateOnly.FromDateTime(from.UtcDateTime);
		var lastDay = DateOnly.FromDateTime(to.AddTicks(-1).UtcDateTime);
		var dayCount = lastDay.DayNumber - firstDay.DayNumber + 1;
		if (dayCount > _options.MaxTickDaysPerRequest)
			throw new TInvestRequestException($"Range covers {dayCount} UTC days; the limit is {_options.MaxTickDaysPerRequest}; request a shorter range.");

		var uid = symbol.Instrument.Short.Uid;
		var today = DateOnly.FromDateTime(now.UtcDateTime);
		var days = Enumerable.Range(0, dayCount).Select(i => firstDay.AddDays(i)).ToList();
		var states = new Dictionary<DateOnly, DayState>();
		var needed = new List<DateOnly>();
		foreach (var day in days.Where(d => d < today))
		{
			bool empty;
			lock (_tickLock) empty = _provenEmpty.Contains((uid, day));
			if (ArchiveCache.TryGet(uid, day, out var path)) states[day] = new(DayKind.File, path);
			else if (empty) states[day] = new(DayKind.Empty, null);
			else needed.Add(day);
		}

		TInvestRateLimiter.Reservation? archives = null, candles = null, lastTrades = null;
		var reserved = false;
		try
		{
			try
			{
				if (needed.Count > 0)
				{
					archives = await _gateway.ReserveArchivesAsync(needed.Count, deadline, ct).ConfigureAwait(false);
					candles = await _gateway.ReserveCandlesAsync(needed.Count, deadline, ct).ConfigureAwait(false);
				}

				if (needed.Count > 0 || days.Contains(today)) lastTrades = await _gateway.ReserveCandlesAsync(1, deadline, ct).ConfigureAwait(false);
				reserved = true;
			}
			catch (TInvestRequestException ex) when (ex.Kind == TInvestFailureKind.Limit)
			{
				// The plan does not fit the budget: fetch in the background, so the client's retry finds the files.
				archives?.Dispose();
				candles?.Dispose();
				lastTrades?.Dispose();
				archives = candles = lastTrades = null;
				if (needed.Count == 0) throw;
			}

			if (needed.Count > 0)
			{
				var fetches = needed.Select(day => GetOrStartFetch(symbol, day, reserved ? archives : null, reserved ? candles : null)).ToArray();
				var all = Task.WhenAll(fetches);
				RegisterFill(all, reserved ? archives : null, reserved ? candles : null);
				archives = candles = null; // Owned by the fill from here on.
				if (!reserved) throw RetryIn(fetches.Length);
				try
				{
					await all.WaitAsync(Max(deadline - _clock.GetUtcNow()), _clock, ct).ConfigureAwait(false);
				}
				catch (TimeoutException)
				{
					throw RetryIn(fetches.Count(f => !f.IsCompleted));
				}
				catch (Exception ex) when (ex is not (OperationCanceledException or TInvestRequestException))
				{
					throw new TInvestRequestException("T-Invest source request failed.");
				}

				for (var i = 0; i < needed.Count; i++) states[needed[i]] = fetches[i].Result;
			}

			return await AssembleAsync(symbol, uid, from, to, openEnd, today, days, states, lastTrades, deadline, ct).ConfigureAwait(false);
		}
		finally
		{
			archives?.Dispose();
			candles?.Dispose();
			lastTrades?.Dispose();
		}
	}

	/// <summary>Multiset union: a trade present in both lists appears as often as in the list holding it more often.</summary>
	internal static List<TInvestTick> Merge(List<TInvestTick> recorded, List<TInvestTick> recent)
	{
		var counts = new Dictionary<TInvestTick, int>();
		foreach (var tick in recorded) counts[tick] = counts.GetValueOrDefault(tick) + 1;
		var seen = new Dictionary<TInvestTick, int>();
		var result = new List<TInvestTick>(recorded);
		foreach (var tick in recent)
		{
			var n = seen[tick] = seen.GetValueOrDefault(tick) + 1;
			if (n > counts.GetValueOrDefault(tick)) result.Add(tick);
		}

		return result.Select((tick, index) => (tick, index)).OrderBy(p => p.tick.Time.Seconds).ThenBy(p => p.tick.Time.Nanos).ThenBy(p => p.index).Select(p => p.tick).ToList();
	}

	/// <summary>The first part of [a, b) not inside the union of the intervals, or null.</summary>
	internal static (DateTimeOffset Start, DateTimeOffset End)? FirstGap(DateTimeOffset a, DateTimeOffset b, IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> covered)
	{
		var cursor = a;
		foreach (var (start, end) in covered.OrderBy(c => c.Start))
		{
			if (end <= cursor) continue;
			if (start > cursor) return (cursor, start < b ? start : b);
			cursor = end;
			if (cursor >= b) return null;
		}

		return cursor < b ? (cursor, b) : null;
	}

	static DateTimeOffset Midnight(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

	static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

	static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

	static TimeSpan Max(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;

	static TInvestRequestException RetryIn(int remaining) =>
		new($"T-Invest trade archives are being fetched; retry in {Math.Clamp((int)Math.Ceiling(Math.Max(1, remaining) * 60.0 / ArchivesPerMinute) + 2, 1, 300)} s.", TInvestFailureKind.Limit);

	async Task<TInvestTickReader> AssembleAsync(TInvestSymbol symbol, string uid, DateTimeOffset from, DateTimeOffset to, bool openEnd, DateOnly today, List<DateOnly> days, Dictionary<DateOnly, DayState> states,
		TInvestRateLimiter.Reservation? lastTrades, DateTimeOffset deadline, CancellationToken ct)
	{
		var leases = new List<IDisposable>();
		try
		{
			return await AssembleCoreAsync(symbol, uid, from, to, openEnd, today, days, states, lastTrades, deadline, leases, ct).ConfigureAwait(false);
		}
		catch
		{
			foreach (var lease in leases) lease.Dispose();
			throw;
		}
	}

	async Task<TInvestTickReader> AssembleCoreAsync(TInvestSymbol symbol, string uid, DateTimeOffset from, DateTimeOffset to, bool openEnd, DateOnly today, List<DateOnly> days, Dictionary<DateOnly, DayState> states,
		TInvestRateLimiter.Reservation? lastTrades, DateTimeOffset deadline, List<IDisposable> leases, CancellationToken ct)
	{
		var dealer = _options.IncludeDealerTrades;
		var segments = new List<Func<CancellationToken, List<TInvestTick>>>();
		long total = 0;
		var recordedDays = days.Where(d => d == today || states[d].Kind == DayKind.Missing).ToList();
		if (recordedDays.Count > 0 && dealer)
			throw new TInvestRequestException("T-Invest ticks for a day without an archive are recorded without dealer trades; set IncludeDealerTrades to false for them.");

		var callNow = _clock.GetUtcNow();
		// The days were planned for the planning day. If the UTC day changed while the answer was prepared, plan again.
		if (openEnd && (DateOnly.FromDateTime(callNow.UtcDateTime) != today || !days.Contains(today)))
			throw new TInvestRequestException("The UTC day changed while the T-Invest ticks were prepared; retry in 2 s.", TInvestFailureKind.Limit);
		// An open end means "now" when the answer is assembled (Appendix A-6), not when the request was planned.
		if (openEnd && days.Contains(today)) to = Min(callNow, Midnight(today).AddDays(1));
		var span = (DateOnly day) => (Start: Max(from, Midnight(day)), End: Min(to, Midnight(day).AddDays(1)));
		var hourFrom = callNow.AddHours(-1);
		IReadOnlyList<TInvestTrade> hour = Array.Empty<TInvestTrade>();
		var usesHour = lastTrades != null && recordedDays.Any(d => span(d).End > hourFrom);
		if (usesHour)
		{
			try
			{
				hour = await _gateway.GetLastTradesAsync(uid, hourFrom, callNow, lastTrades!, deadline, ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_stop.IsCancellationRequested)
			{
				throw Limited();
			}
		}

		var none = Array.Empty<(DateTimeOffset Start, DateTimeOffset End)>();
		foreach (var day in days)
		{
			var (a, b) = span(day);
			if (recordedDays.Contains(day))
			{
				var recording = Volatile.Read(ref _recording);
				var coverage = recording?.Coverage(uid, day) ?? none;
				var gap = FirstGap(a, b, coverage.Concat(usesHour ? new[] { (hourFrom, callNow) } : none));
				if (gap != null)
				{
					if (day != today) throw new TInvestRequestException($"no T-Invest trade archive for {day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
					var firstCovered = coverage.Select(c => (DateTimeOffset?)c.Start).DefaultIfEmpty(null).Min();
					throw new TInvestRequestException($"T-Invest ticks for {gap.Value.Start:HH:mm:ss}\u2013{gap.Value.End:HH:mm:ss} UTC today are not available; " +
						(firstCovered == null ? "the bridge has not recorded this instrument today." : $"the bridge recorded this instrument from {firstCovered:HH:mm:ss}."));
				}

				var recordedTrades = recording?.Read(uid, day, a, b) ?? Array.Empty<TInvestTrade>();
				// Cleanup can prune a day between the coverage check and the read; then the coverage is gone and so are the trades.
				if (FirstGap(a, b, (recording?.Coverage(uid, day) ?? none).Concat(usesHour ? new[] { (hourFrom, callNow) } : none)) != null)
					throw new TInvestRequestException("T-Invest recorded ticks changed while the answer was prepared; request the range again.");
				var recorded = recordedTrades.Where(t => t.IsExchange).Select(t => new TInvestTick(t.Time, t.Price, t.Lots, t.Direction)).ToList();
				var recent = hour.Where(t => t.IsExchange && t.Time.Utc >= a && t.Time.Utc < b).Select(t => new TInvestTick(t.Time, t.Price, t.Lots, t.Direction)).ToList();
				var merged = Merge(recorded, recent);
				total += merged.Count;
				if (merged.Count > 0) segments.Add(_ => merged);
			}
			else if (states[day].Kind == DayKind.File)
			{
				var path = states[day].Path!;
				var whole = a == Midnight(day) && b == Midnight(day).AddDays(1);
				leases.Add(ArchiveCache.TryPin(path) ?? throw RetryIn(1));
				var count = await CountArchiveAsync(path, day, dealer, whole, a, b, deadline, ct).ConfigureAwait(false);
				total += count;
				// A day with no ticks in range is never loaded.
				if (count > 0)
				{
					segments.Add(token =>
					{
						BeforeArchiveLoad?.Invoke(path);
						return TradeArchiveReader.Load(path, day, dealer, a, b, token);
					});
				}
			}
		}

		if (total > _options.MaxTicksPerRequest)
			throw new TInvestRequestException($"Range holds {total} ticks; the limit is {_options.MaxTicksPerRequest}; request a shorter range.");
		return new TInvestTickReader(segments, symbol.Instrument.Short.Lot, total, _clock, leases);
	}

	// The count runs on the store's own token and is remembered, so a count that outlasts the request's budget
	// finishes in the background and the client's retry finds it.
	async Task<int> CountArchiveAsync(string path, DateOnly day, bool dealer, bool whole, DateTimeOffset a, DateTimeOffset b, DateTimeOffset deadline, CancellationToken ct)
	{
		try
		{
			var length = new FileInfo(path).Length;
			var key = (path, length, dealer, whole ? 0L : a.UtcTicks, whole ? 0L : b.UtcTicks);
			Task<int> task;
			lock (_tickLock)
			{
				if (!_countTasks.TryGetValue(key, out task!) || task.IsFaulted || task.IsCanceled)
				{
					if (_countTasks.Count >= 256) _countTasks.Clear();
					task = _countTasks[key] = Task.Run(() =>
					{
						BeforeArchiveCount?.Invoke(path);
						return TradeArchiveReader.Count(path, day, dealer, whole ? null : a, whole ? null : b, _options.MaxTicksPerRequest, _stop.Token);
					}, _stop.Token);
				}
			}

			return await task.WaitAsync(Max(deadline - _clock.GetUtcNow()), _clock, ct).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			throw RetryIn(1);
		}
		catch (TInvestRequestException)
		{
			// A damaged cached file never serves: the next request downloads it again.
			ArchiveCache.Delete(path);
			throw;
		}
		catch (IOException)
		{
			throw new TInvestRequestException("T-Invest trade archive could not be read.");
		}
	}

	Task<DayState> GetOrStartFetch(TInvestSymbol symbol, DateOnly day, TInvestRateLimiter.Reservation? archives, TInvestRateLimiter.Reservation? candles)
	{
		var key = (symbol.Instrument.Short.Uid, day);
		lock (_tickLock)
		{
			if (_fetches.TryGetValue(key, out var running)) return running;
			var task = Task.Run(() => FetchDayAsync(symbol, day, archives, candles));
			_fetches[key] = task;
			task.ContinueWith(_ =>
			{
				lock (_tickLock) _fetches.Remove(key);
			}, TaskScheduler.Default);
			return task;
		}
	}

	void RegisterFill(Task all, params TInvestRateLimiter.Reservation?[] owned)
	{
		lock (_tickLock) _fills.Add(all);
		all.ContinueWith(_ =>
		{
			foreach (var reservation in owned) reservation?.Dispose();
			lock (_tickLock) _fills.Remove(all);
		}, TaskScheduler.Default);
	}

	async Task<DayState> FetchDayAsync(TInvestSymbol symbol, DateOnly day, TInvestRateLimiter.Reservation? archives, TInvestRateLimiter.Reservation? candles)
	{
		var uid = symbol.Instrument.Short.Uid;
		var background = _clock.GetUtcNow().AddMinutes(BackgroundFillMinutes);
		// Background-only work waits on the archive window for long; it must not hold the foreground's permits.
		var gate = archives == null ? _backgroundGate : _fetchGate;
		await gate.WaitAsync(_stop.Token).ConfigureAwait(false);
		try
		{
			var path = ArchiveCache.PathFor(uid, day);
			var id = symbol.Instrument.Short.Ticker + "_" + symbol.Instrument.Short.ClassCode;
			if (await _gateway.DownloadTradeArchiveAsync(id, day, path, archives, background, _stop.Token).ConfigureAwait(false))
			{
				if (new FileInfo(path).Length > _options.ArchiveCacheMaxBytes)
				{
					// One file bigger than the whole cap cannot be kept without breaking the cap.
					ArchiveCache.Delete(path);
					throw new TInvestRequestException("A T-Invest trade archive is larger than ArchiveCacheMaxBytes; raise the cache size cap.");
				}

				ArchiveCache.Evict(path);
				var newest = ArchiveCache.NewestDay();
				if (newest != null) Volatile.Read(ref _recording)?.DeleteBefore(newest.Value);
				return new(DayKind.File, path);
			}

			// No archive: only exchange candles can show that the day had no trading.
			var first = symbol.Instrument.FirstMinuteCandle;
			var start = Midnight(day);
			var own = candles == null ? await _gateway.ReserveCandlesAsync(1, background, _stop.Token).ConfigureAwait(false) : null;
			try
			{
				var found = await _gateway.GetCandlesAsync(new(uid, start, start.AddDays(1), 60, 2400, false), candles ?? own!, background, _stop.Token).ConfigureAwait(false);
				var traded = found.Any(c => c.Exchange && c.Time >= start && c.Time < start.AddDays(1));
				if (!traded && first != null && day >= DateOnly.FromDateTime(first.Value.UtcDateTime))
				{
					// Candles of a day that just ended can still lag, and its archive may appear later: remember only older days.
					if (day <= DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime).AddDays(-2))
						lock (_tickLock) _provenEmpty.Add((uid, day));
					return new(DayKind.Empty, null);
				}

				return new(DayKind.Missing, null);
			}
			finally
			{
				own?.Dispose();
			}
		}
		finally
		{
			gate.Release();
		}
	}

	sealed record DayState(DayKind Kind, string? Path);
}

/// <summary>Streams the prepared ticks in batches. Days are loaded one at a time.</summary>
public sealed class TInvestTickReader : ISymbolHistoryStoreReader
{
	public const int BatchSize = 2000;

	static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(20);

	readonly Queue<Func<CancellationToken, List<TInvestTick>>> _segments;
	readonly int _lot;
	readonly TimeProvider _clock;
	readonly List<IDisposable> _leases;
	List<TInvestTick> _current = new();
	int _index;

	internal TInvestTickReader(IEnumerable<Func<CancellationToken, List<TInvestTick>>> segments, int lot, long total, TimeProvider clock, List<IDisposable> leases)
	{
		_segments = new(segments);
		_lot = lot;
		Total = total;
		_clock = clock;
		_leases = leases;
	}

	/// <summary>The number of ticks the answer holds, known before the first record.</summary>
	public long Total { get; }

	public async Task<SymbolHistoryResponse> ReadSymbolHistoryAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		while (_index >= _current.Count && _segments.Count > 0)
		{
			var load = _segments.Dequeue();
			// Each day's load has the same budget as a candle read; past it the connection closes without a final record.
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			var work = Task.Run(() => load(linked.Token), linked.Token);
			try
			{
				_current = await work.WaitAsync(ReadBudget, _clock, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
			{
				// The worker may still be inside a synchronous read: stop it and do not wait for it.
				linked.Cancel();
				_ = work.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
				throw new TInvestRequestException("T-Invest tick history read timed out.");
			}

			_index = 0;
		}

		var take = Math.Min(BatchSize, _current.Count - _index);
		var records = new List<ISymbolHistoryRecord>(take);
		for (var i = 0; i < take; i++)
		{
			var tick = _current[_index + i];
			records.Add(new TInvestTickRecord(tick.Time.Microseconds, tick.Price, (double)tick.Lots * _lot, tick.Direction));
		}

		_index += take;
		return new(records, _index < _current.Count || _segments.Count > 0);
	}

	public void Dispose()
	{
		foreach (var lease in _leases) lease.Dispose();
	}
}
