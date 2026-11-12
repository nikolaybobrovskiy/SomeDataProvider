namespace SomeDataProvider.DtcProtocolServer.Data;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using NBLib.Logging;

using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DataStorage.TInvest;

using SomeDataProvider.DtcProtocolServer.Market;

sealed partial class StoresProvider : ISymbolHistoryStoreProvider, ISymbolsStoreProvider, IDisposable
{
	public const string DataSourceFredPrefix = "fred";

	public const char DataSourcePrefixSeparator = '-';

	/// <summary>Pause between background startup checks, like the watch list's retry.</summary>
	public static readonly TimeSpan StartupRetry = TimeSpan.FromSeconds(30);

	static readonly TimeSpan StartupReportInterval = TimeSpan.FromMinutes(10);

	readonly ILogger<StoresProvider> _l;
	readonly Lazy<DataStorage.Fred.Store> _lazyFredStore;
	readonly Func<ITInvestGateway>? _createGateway;
	readonly TInvestOptions? _startupOptions;
	readonly CancellationTokenSource _stopStartup = new();
	readonly object _startLock = new();
	readonly TimeProvider _time = TimeProvider.System;
	TInvestStore? _tInvestStore;
	string? _tInvestEnvironment;
	bool _initialized;
	bool _failedForGood;
	Task _startup = Task.CompletedTask;

	public StoresProvider(StoresOptions opts, ILoggerFactory loggerFactory, TInvestToken? token = null, Func<ITInvestGateway>? gatewayFactory = null, TimeProvider? timeProvider = null)
	{
		if (opts == null) throw new ArgumentNullException(nameof(opts));
		if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));
		_l = loggerFactory.CreateLogger<StoresProvider>();
		_time = timeProvider ?? TimeProvider.System;
		_lazyFredStore = new (() => new DataStorage.Fred.Store(opts.KnownFred.KnownApiKey, loggerFactory));
		var options = opts.TInvest ?? new TInvestOptions();
		options.Validate();
		_startupOptions = options;
		if (options.Enabled && token != null) _createGateway = gatewayFactory ?? (() => new SdkTInvestGateway(token, options, new TInvestRateLimiter(), logger: loggerFactory.CreateLogger<SdkTInvestGateway>()));
	}

	/// <summary>
	/// For tests: uses this store instead of building one from <see cref="StoresOptions"/>, so a test can give it a
	/// <see cref="DataStorage.Fred.Service"/> built on a fake HttpMessageHandler, with no real API key or network call.
	/// </summary>
	internal StoresProvider(DataStorage.Fred.Store fredStore, ILoggerFactory loggerFactory)
	{
		if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));
		_l = loggerFactory.CreateLogger<StoresProvider>();
		_lazyFredStore = new (() => fredStore);
	}

	internal StoresProvider(DataStorage.Fred.Store? fredStore, TInvestStore tInvestStore, ILoggerFactory loggerFactory)
	{
		_l = loggerFactory.CreateLogger<StoresProvider>();
		_lazyFredStore = new(() => fredStore ?? throw new InvalidOperationException("No test FRED store."));
		_tInvestStore = tInvestStore;
		_tInvestEnvironment = "sandbox";
		MarketDataHub = new(tInvestStore.Gateway, tInvestStore.Options, tInvestStore.Clock, persistRecording: false);
		tInvestStore.UseTickRecording(MarketDataHub.Recording);
		WatchListTask = MarketDataHub.StartWatchListAsync(tInvestStore);
	}

	/// <summary>Raised once, when the background startup check first passes and the T-Invest source exists.</summary>
	public event Action? TInvestStarted;

	public TInvestMarketDataHub? MarketDataHub { get; private set; }
	public string SourcesDescription => _tInvestStore == null ? "FRED" : $"FRED, T-Invest ({_tInvestEnvironment})";
	internal Task WatchListTask { get; private set; } = Task.CompletedTask;

	internal Task StartupTask => _startup;

	/// <summary>Test seam: runs between the decision to answer "starting" and the answer itself.</summary>
	internal Action? BeforeStartingReject { get; set; }

	/// <summary>The source exists and its store is published.</summary>
	internal bool TInvestReady => Volatile.Read(ref _tInvestStore) != null;

	/// <summary>The startup check has not passed yet, and it may still pass: it is configured and has not failed for good.</summary>
	internal bool TInvestPending => _createGateway != null && !_failedForGood && Volatile.Read(ref _tInvestStore) == null;

	DataStorage.Fred.Store FredStore => _lazyFredStore.Value;

	// Called after the CLI attaches its logger, before either listener starts. The startup check runs in the
	// background, so the server (and FRED) never waits for T-Invest.
	public void Initialize()
	{
		if (_initialized || _createGateway == null || _startupOptions == null) return;
		_initialized = true;
		_l.LogInformation("T-Invest source: {environment}, token from {source}; checking access.", _startupOptions.Environment, _startupOptions.TokenSource);
		_startup = Task.Run(RunStartupAsync);
	}

	public ValueTask<ISymbolHistoryStore> GetSymbolHistoryStoreAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default)
	{
		_l.LogInformation("GetSymbolHistoryStore({symbolCode})", symbol.Code);
		var dataService = symbol.DataService;
		switch (dataService)
		{
			case DataService.Fred:
				return new ValueTask<ISymbolHistoryStore>(FredStore);
			default:
				throw new NotImplementedException($"Store is not implemented: {dataService}");
		}
	}

	public ValueTask<(ISymbolsStore SymbolsStore, string SymbolCode)?> GetSymbolsStoreAsync(string code, CancellationToken cancellationToken = default) =>
		GetSymbolsStoreAsync(code, string.Empty, cancellationToken);

	public async ValueTask<(ISymbolsStore SymbolsStore, string SymbolCode)?> GetSymbolsStoreAsync(string code, string exchange, CancellationToken cancellationToken = default)
	{
		if (code.StartsWith("fred-", StringComparison.Ordinal))
			return (FredStore, code[5..]);
		var tInvestStore = Volatile.Read(ref _tInvestStore);
		if (tInvestStore == null)
		{
			// Not "unknown symbol": the check is still being retried, or has not been started yet.
			if (_createGateway != null && !_failedForGood)
			{
				BeforeStartingReject?.Invoke();
				throw new TInvestRequestException($"T-Invest source is starting; retry in {(int)StartupRetry.TotalSeconds} s.", TInvestFailureKind.Starting);
			}

			return null;
		}

		if (exchange.Length != 0) return (tInvestStore, TInvestStore.LookupCode(code, exchange));
		var separator = code.LastIndexOf('-');
		if (separator <= 0 || separator == code.Length - 1 || code.StartsWith("fred", StringComparison.OrdinalIgnoreCase)) return null;
		var ticker = code[..separator];
		var classCode = code[(separator + 1)..];
		// Discover a cold class code through an exact lookup, not a guessed ticker-only match.
		// Both successful discovery and misses share the store's cache (negative entries: ten minutes).
		var symbol = await tInvestStore.ResolveAsync(ticker, classCode, tInvestStore.Clock.GetUtcNow().AddSeconds(8), cancellationToken);
		return symbol == null ? null : (tInvestStore, TInvestStore.LookupCode(ticker, classCode));
	}

	public async ValueTask<IReadOnlyCollection<ISymbol>> GetKnownSymbolsAsync(CancellationToken cancellationToken = default)
	{
		// TODO: Cache. Log cache (with LogOperation without Async or just LogInformation)?
		return await _l.LogOperationAsync(async () =>
		{
			return (await FredStore.GetKnownSymbolsAsync(cancellationToken))
				.Select(symbol => new Symbol(DataSourceFredPrefix, symbol)).ToArray();
		}, "GetKnownSymbols()");
	}

	public async ValueTask<IReadOnlyCollection<ISymbol>> SearchSymbolsAsync(string searchText, SymbolSearchType searchType, int limit, CancellationToken cancellationToken = default)
		=> await SearchSymbolsAsync(searchText, searchType, limit, string.Empty, SymbolType.Unset, cancellationToken);

	public async ValueTask<IReadOnlyCollection<ISymbol>> SearchSymbolsAsync(string searchText, SymbolSearchType searchType, int limit, string exchange, SymbolType securityType, CancellationToken cancellationToken = default)
	{
		var result = await SearchWithStatusAsync(searchText, searchType, limit, exchange, securityType, cancellationToken);
		if (!result.HasSuccessfulSource || (result.Symbols.Count == 0 && result.Failures.Count > 0)) throw new TInvestRequestException(result.UnavailableText);
		return result.Symbols;
	}

	public void Dispose()
	{
		lock (_startLock) _stopStartup.Cancel();
		try { _startup.Wait(TimeSpan.FromSeconds(5)); }
		catch (AggregateException) { /* The loop has already reported its failure. */ }

		MarketDataHub?.Dispose();
		if (_lazyFredStore.IsValueCreated) _lazyFredStore.Value.Dispose();
		_tInvestStore?.Dispose();
	}

	internal async ValueTask<SearchResult> SearchWithStatusAsync(string searchText, SymbolSearchType searchType, int limit, string exchange, SymbolType securityType, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(searchText)) throw new TInvestRequestException("SearchText must not be empty.");
		_l.LogInformation("SearchSymbols({searchText},{searchType},{limit})", searchText, searchType, limit);
		var fredPrefix = $"{DataSourceFredPrefix}{DataSourcePrefixSeparator}";
		var fredOnly = searchText.StartsWith(fredPrefix, StringComparison.Ordinal);
		var normalizedSearchText = fredOnly ? searchText.Substring(fredPrefix.Length) : searchText;
		var result = new List<ISymbol>();
		var failures = new List<SearchFailure>();
		var succeeded = false;
		limit = Math.Clamp(limit, 0, 50);
		if (limit == 0) return new(result, failures, true);
		void Failed(string source, Exception error)
		{
			// Only stable local classifications cross the wire or enter the log, never upstream payloads.
			var reason = error is TInvestRequestException request ? request.Kind switch
			{
				TInvestFailureKind.Starting => "SourceStarting",
				TInvestFailureKind.Limit => "LimitRejected",
				_ => "SourceUnavailable",
			} : "SourceUnavailable";
			failures.Add(new(source, reason, (error as TInvestRequestException)?.RetryAfterSeconds));
			_l.LogWarning("Symbol search source {source} failed: {reason}", source, reason);
		}
		var pending = TInvestPending;
		var tInvestStore = Volatile.Read(ref _tInvestStore);
		if (!fredOnly && (tInvestStore != null || pending))
		{
			try
			{
				if (tInvestStore == null) throw new TInvestRequestException("T-Invest source is starting.", TInvestFailureKind.Starting, retryAfterSeconds: (int)StartupRetry.TotalSeconds);
				result.AddRange(await tInvestStore.SearchAsync(searchText, searchType, limit, exchange, securityType, cancellationToken));
				succeeded = true;
			}
			catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { Failed("t-invest", ex); }
		}
		cancellationToken.ThrowIfCancellationRequested();
		if (result.Count < limit && exchange.Length == 0 && securityType == SymbolType.Unset)
		{
			try
			{
				var matches = await FredStore.SearchSymbolsAsync(normalizedSearchText, searchType, limit - result.Count, cancellationToken);
				result.AddRange(matches.Select(symbol => (ISymbol)new Symbol(DataSourceFredPrefix, symbol)));
				succeeded = true;
			}
			catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { Failed("fred", ex); }
		}
		cancellationToken.ThrowIfCancellationRequested();
		// No eligible source (for example a disabled T-Invest source with an exchange filter) is no matches.
		return new(result, failures, succeeded || failures.Count == 0);
	}

	internal TInvestStore? GetTInvestInformationStore()
	{
		var store = Volatile.Read(ref _tInvestStore);
		if (store == null && TInvestPending)
			throw new TInvestRequestException("T-Invest source is starting.", TInvestFailureKind.Starting);
		return store;
	}

	async Task RunStartupAsync()
	{
		var options = _startupOptions!;
		var attempts = 0;
		DateTimeOffset? lastReport = null;
		while (!_stopStartup.IsCancellationRequested)
		{
			attempts++;
			ITInvestGateway? gateway = null;
			string failureClass;
			try
			{
				gateway = _createGateway!();
				await TInvestStartup.CheckAsync(gateway, options, _time.GetUtcNow().AddSeconds(8), _stopStartup.Token).ConfigureAwait(false);
				CompleteStartup(gateway, options, attempts);
				return;
			}
			catch (OperationCanceledException) when (_stopStartup.IsCancellationRequested)
			{
				gateway?.Dispose();
				return;
			}
			catch (Exception ex)
			{
				failureClass = (gateway as SdkTInvestGateway)?.LastFailureStatus ?? ex.GetType().Name;
				gateway?.Dispose();
				if (ex is TInvestRequestException { Kind: TInvestFailureKind.Configuration } or ArgumentException or InvalidOperationException)
				{
					// The operator has to fix this; retrying cannot help.
					_failedForGood = true;
					_l.LogWarning("T-Invest source is not configured: {reason}", ex is TInvestRequestException ? ex.Message : "Startup access check failed.");
					return;
				}
			}

			var now = _time.GetUtcNow();
			if (lastReport == null)
			{
				_l.LogWarning("T-Invest startup check failed ({failure}); the server keeps running and retries every {seconds} seconds.", failureClass, (int)StartupRetry.TotalSeconds);
				lastReport = now;
			}
			else if (now - lastReport >= StartupReportInterval)
			{
				_l.LogWarning("T-Invest startup check still failing ({failure}) after {attempts} attempts; retrying every {seconds} seconds.", failureClass, attempts, (int)StartupRetry.TotalSeconds);
				lastReport = now;
			}

			try
			{
				await Task.Delay(StartupRetry, _time, _stopStartup.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	// Creates the store and the hub once. A success racing with disposal or with another
	// attempt creates nothing, and the caller disposes its gateway.
	void CompleteStartup(ITInvestGateway gateway, TInvestOptions options, int attempts)
	{
		lock (_startLock)
		{
			if (_stopStartup.IsCancellationRequested || _tInvestStore != null)
			{
				gateway.Dispose();
				return;
			}

			var store = new TInvestStore(gateway, options);
			var hub = new TInvestMarketDataHub(gateway, options);
			store.UseTickRecording(hub.Recording);
			hub.Diagnostic += text => _l.LogInformation("T-Invest live: {status}; gRPC {grpcStatus}, code {code}.", text, (gateway as SdkTInvestGateway)?.LastFailureStatus, (gateway as SdkTInvestGateway)?.LastFailureCode);
			hub.WatchListRetrying += name => _l.LogWarning("T-Invest watch-list entry is unavailable; retrying in 30 seconds: {symbol}", name);
			MarketDataHub = hub;
			WatchListTask = hub.StartWatchListAsync(store);
			_tInvestEnvironment = options.Environment.ToLowerInvariant();
			Volatile.Write(ref _tInvestStore, store);
		}

		_l.LogInformation("T-Invest source: {environment}, token from {source}; startup check passed after {attempts} attempt(s).", options.Environment, options.TokenSource, attempts);
		// A failing subscriber must not undo a startup that has already been published.
		foreach (var handler in (TInvestStarted?.GetInvocationList() ?? Array.Empty<Delegate>()).Cast<Action>())
		{
			try { handler(); }
			catch (Exception ex) { _l.LogWarning(ex, "A T-Invest startup subscriber failed."); }
		}
	}

	public class StoresOptions
	{
		public FredOptions? Fred { get; init; }

		public TInvestOptions? TInvest { get; init; }

		public FredOptions KnownFred => Fred ?? throw new InvalidOperationException($"{nameof(Fred)} is not set.");

		public class FredOptions
		{
			public string? ApiKey { get; init; }

			public DataStorage.Fred.ServiceApiKey KnownApiKey => !string.IsNullOrEmpty(ApiKey) ? (DataStorage.Fred.ServiceApiKey)ApiKey : throw new InvalidOperationException($"{nameof(Fred)}.{nameof(ApiKey)} is not set. Configure it in appsettings.Local.json (see appsettings.Local.example.json) or the {nameof(StoresOptions)}:{nameof(Fred)}:{nameof(ApiKey)} configuration section.");
		}
	}

	internal sealed record SearchFailure(string Source, string ReasonCode, int? RetryAfterSeconds);
	internal sealed record SearchResult(IReadOnlyCollection<ISymbol> Symbols, IReadOnlyList<SearchFailure> Failures, bool HasSuccessfulSource)
	{
		public string UnavailableText
		{
			get
			{
				var retry = Failures.Max(f => f.RetryAfterSeconds);
				return retry is > 0 ? $"Symbol search sources are unavailable; retry in {retry} s." : "Symbol search sources are unavailable.";
			}
		}
	}

	class Symbol : ISymbol
	{
		readonly ISymbol _s;
		readonly string _p;

		public Symbol(string prefix, ISymbol symbol)
		{
			_s = symbol;
			_p = prefix;
		}

		public string Code => $"{_p}{DataSourcePrefixSeparator}{_s.Code}";

		public string? Exchange => _s.Exchange;

		public SymbolType Type => _s.Type;

		public string? Description => _s.Description;

		public string? Category => _s.Category;

		public int NumberOfDecimals => _s.NumberOfDecimals;

		public float MinPriceIncrement => _s.MinPriceIncrement;

		public string? Currency => _s.Currency;

		public bool IsRealTime => _s.IsRealTime;

		public bool IsDelayed => _s.IsDelayed;

		public bool IsDiscontinued => _s.IsDiscontinued;

		public DataService DataService => _s.DataService;

		public string? DataServiceSettings => _s.DataServiceSettings;
	}
}
