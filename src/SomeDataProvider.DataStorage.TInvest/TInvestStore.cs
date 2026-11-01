namespace SomeDataProvider.DataStorage.TInvest;

using SomeDataProvider.DataStorage.Definitions;

/// <summary>Complete definitions only, refreshed daily at 03:00 UTC and on use after a failed refresh.</summary>
public sealed partial class TInvestStore : ISymbolsStore, IDisposable
{
	readonly ITInvestGateway _gateway;
	readonly TInvestOptions _options;
	readonly TimeProvider _clock;
	readonly object _cacheLock = new();
	readonly Dictionary<(string Ticker, string ClassCode), LookupGate> _lookups = new();
	readonly Dictionary<(string Ticker, string ClassCode), Entry> _cache = new();
	readonly CancellationTokenSource _stop = new();
	readonly ITimer _refreshTimer;
	int _disposed;

	public TInvestStore(ITInvestGateway gateway, TInvestOptions options, TimeProvider? clock = null)
	{
		options.Validate();
		_gateway = gateway;
		_options = options;
		_clock = clock ?? TimeProvider.System;
		_refreshTimer = _clock.CreateTimer(_ => RefreshTask = RefreshAsync(), null, UntilRefresh(), Timeout.InfiniteTimeSpan);
	}

	internal Task RefreshTask { get; private set; } = Task.CompletedTask;

	public static string LookupCode(string ticker, string classCode) => ticker + "\t" + classCode;

	public static bool Complete(TInvestInstrument instrument) => instrument.Short.Kind != TInvestInstrumentKind.Unsupported &&
		instrument.Short.Uid.Length > 0 && instrument.Short.Lot > 0 && instrument.Increment > 0 &&
		instrument.Currency.Length == 3 && instrument.Currency.All(char.IsAsciiLetter) &&
		(instrument.Short.Kind != TInvestInstrumentKind.Future || (instrument.IncrementAmount > 0 && instrument.BasicAssetSize > 0)) &&
		(instrument.Short.Kind != TInvestInstrumentKind.Bond || instrument.Nominal > 0) &&
		(!instrument.Expiration.HasValue || instrument.Expiration.Value.ToUnixTimeSeconds() is >= 0 and <= uint.MaxValue);

	public async ValueTask<ISymbol?> GetSymbolAsync(string code, CancellationToken cancellationToken = default)
	{
		var separator = code.LastIndexOf('\t');
		if (separator <= 0 || separator == code.Length - 1) return null;
		return await ResolveAsync(code[..separator], code[(separator + 1)..], _clock.GetUtcNow().AddSeconds(8), cancellationToken).ConfigureAwait(false);
	}

	public async Task<TInvestSymbol?> ResolveAsync(string ticker, string classCode, DateTimeOffset deadline, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		using var budget = NewBudget(deadline, cancellationToken);
		if (TryCached(ticker, classCode, out var cached)) return cached;
		var key = (ticker, classCode);
		LookupGate gate;
		lock (_cacheLock)
		{
			if (!_lookups.TryGetValue(key, out gate!)) _lookups[key] = gate = new LookupGate();
			gate.Users++;
		}
		var entered = false;
		try
		{
			await gate.Semaphore.WaitAsync(budget.Token).ConfigureAwait(false);
			entered = true;
			if (TryCached(ticker, classCode, out cached)) return cached;
			var instrument = await _gateway.GetInstrumentAsync(ticker, classCode, deadline, budget.Token).ConfigureAwait(false);
			if (instrument != null && (instrument.Short.Ticker != ticker || instrument.Short.ClassCode != classCode))
				throw new TInvestRequestException("T-Invest returned an inconsistent instrument identity.");
			var unsupported = instrument != null && !Complete(instrument);
			var symbol = instrument == null || unsupported ? null : new TInvestSymbol(instrument);
			var now = _clock.GetUtcNow();
			var nextRefresh = new DateTimeOffset(now.UtcDateTime.Date.AddHours(3), TimeSpan.Zero);
			if (nextRefresh <= now) nextRefresh = nextRefresh.AddDays(1);
			lock (_cacheLock) _cache[key] = new(symbol, symbol == null ? now.AddMinutes(10) : nextRefresh, unsupported);
			if (unsupported) throw Unsupported();
			return symbol;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
		{
			throw Limited();
		}
		finally
		{
			if (entered) gate.Semaphore.Release();
			lock (_cacheLock)
			{
				if (--gate.Users == 0)
				{
					_lookups.Remove(key);
					gate.Semaphore.Dispose();
				}
			}
		}
	}

	public ValueTask<IReadOnlyCollection<ISymbol>> GetKnownSymbolsAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromResult<IReadOnlyCollection<ISymbol>>(Array.Empty<ISymbol>());

	public ValueTask<IReadOnlyCollection<ISymbol>> SearchSymbolsAsync(string searchText, SymbolSearchType searchType, int limit, CancellationToken cancellationToken = default) =>
		SearchAsync(searchText, searchType, limit, string.Empty, SymbolType.Unset, cancellationToken);

	public async ValueTask<IReadOnlyCollection<ISymbol>> SearchAsync(string text, SymbolSearchType searchType, int limit, string exchange, SymbolType securityType, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (string.IsNullOrWhiteSpace(text)) throw new TInvestRequestException("SearchText must not be empty.");
		limit = Math.Clamp(limit, 0, 50);
		if (limit == 0) return Array.Empty<ISymbol>();
		var deadline = _clock.GetUtcNow().AddSeconds(8);
		using var budget = NewBudget(deadline, cancellationToken);
		IReadOnlyList<TInvestInstrumentShort> found;
		try { found = await _gateway.FindInstrumentsAsync(text, deadline, budget.Token).ConfigureAwait(false); }
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw Limited();
		}
		var candidates = found.Where(i => i.Ticker.Length > 0 && i.ClassCode.Length > 0 && i.Uid.Length > 0 && i.Lot > 0 &&
			i.Kind != TInvestInstrumentKind.Unsupported && (!_options.SearchOnlyApiTradeable || i.ApiTradeAvailable) &&
			(exchange.Length == 0 || exchange == i.ClassCode) &&
			(securityType == SymbolType.Unset || new TInvestSymbol(new(i, 1, "ZZZ")).Type == securityType ||
				(i.Kind == TInvestInstrumentKind.StockOption && securityType is SymbolType.FuturesOption or SymbolType.IndexOption)))
			.DistinctBy(i => (i.Ticker, i.ClassCode));
		if (searchType == SymbolSearchType.BySymbol)
		{
			candidates = candidates.Where(i => i.Ticker.StartsWith(text, StringComparison.OrdinalIgnoreCase))
				.OrderBy(i => i.Ticker.Equals(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
		}
		var result = new List<ISymbol>();
		var cold = new List<TInvestInstrumentShort>();
		// Cached matches first, regardless of their position in the upstream list.
		lock (_cacheLock)
		{
			foreach (var candidate in candidates)
			{
				if (TryCached(candidate.Ticker, candidate.ClassCode, out var cached, rejectUnsupported: false))
				{
					if (cached != null && (securityType == SymbolType.Unset || cached.Type == securityType)) result.Add(cached);
				}
				else
				{
					cold.Add(candidate);
				}
				if (result.Count >= limit) break;
			}
		}
		foreach (var candidate in cold.Take(20))
		{
			if (result.Count >= limit) break;
			if (_clock.GetUtcNow() >= deadline)
			{
				if (result.Count == 0) throw Limited();
				break;
			}
			try
			{
				var symbol = await ResolveAsync(candidate.Ticker, candidate.ClassCode, deadline, cancellationToken).ConfigureAwait(false);
				if (symbol != null && (securityType == SymbolType.Unset || symbol.Type == securityType)) result.Add(symbol);
			}
			catch (TInvestRequestException ex) when (ex.Kind == TInvestFailureKind.Unsupported) { continue; }
			catch (TInvestRequestException ex) when (ex.Kind == TInvestFailureKind.Limit && result.Count > 0) { break; }
		}
		return result.Take(limit).ToArray();
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_refreshTimer.Dispose();
		_stop.Cancel();
		_gateway.Dispose();
	}

	static TInvestRequestException Limited() => new("T-Invest request limit reached; retry in 1 s.", TInvestFailureKind.Limit);
	static TInvestRequestException Unsupported() => new("T-Invest instrument kind or definition is not supported.", TInvestFailureKind.Unsupported);

	TimeSpan UntilRefresh()
	{
		var now = _clock.GetUtcNow();
		var next = new DateTimeOffset(now.UtcDateTime.Date.AddHours(3), TimeSpan.Zero);
		return (next > now ? next : next.AddDays(1)) - now;
	}

	async Task RefreshAsync()
	{
		try
		{
			(string Ticker, string ClassCode)[] keys;
			lock (_cacheLock)
			{
				foreach (var key in _cache.Where(pair => pair.Value.Symbol == null && pair.Value.Expires <= _clock.GetUtcNow()).Select(pair => pair.Key).ToArray()) _cache.Remove(key);
				keys = _cache.Where(pair => pair.Value.Symbol != null).Select(pair => pair.Key).ToArray();
			}
			foreach (var key in keys)
			{
				_stop.Token.ThrowIfCancellationRequested();
				try { await ResolveAsync(key.Ticker, key.ClassCode, _clock.GetUtcNow().AddSeconds(8), _stop.Token).ConfigureAwait(false); }
				catch (TInvestRequestException) { /* Failed entries remain expired and are retried on demand. */ }
			}
		}
		catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
		catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
		finally
		{
			if (Volatile.Read(ref _disposed) == 0)
			{
				try { _refreshTimer.Change(UntilRefresh(), Timeout.InfiniteTimeSpan); }
				catch (ObjectDisposedException) { }
			}
		}
	}

	bool TryCached(string ticker, string classCode, out TInvestSymbol? symbol, bool rejectUnsupported = true)
	{
		lock (_cacheLock)
		{
			if (_cache.TryGetValue((ticker, classCode), out var entry) && entry.Expires > _clock.GetUtcNow())
			{
				if (entry.Unsupported && rejectUnsupported) throw Unsupported();
				symbol = entry.Symbol;
				return true;
			}
			symbol = null;
			return false;
		}
	}

	Budget NewBudget(DateTimeOffset deadline, CancellationToken cancellationToken) => new(deadline - _clock.GetUtcNow(), _clock, cancellationToken, _stop.Token);

	sealed class Budget : IDisposable
	{
		readonly CancellationTokenSource _timer;
		readonly CancellationTokenSource _linked;

		public Budget(TimeSpan remaining, TimeProvider clock, CancellationToken cancellationToken, CancellationToken stop)
		{
			_timer = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, clock);
			_linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timer.Token, stop);
		}

		public CancellationToken Token => _linked.Token;

		public void Dispose()
		{
			_linked.Dispose();
			_timer.Dispose();
		}
	}

	sealed class LookupGate
	{
		public SemaphoreSlim Semaphore { get; } = new(1);
		public int Users { get; set; }
	}

	sealed record Entry(TInvestSymbol? Symbol, DateTimeOffset Expires, bool Unsupported = false);
}
