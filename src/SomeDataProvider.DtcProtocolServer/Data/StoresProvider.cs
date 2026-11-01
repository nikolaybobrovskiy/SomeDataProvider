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

	readonly ILogger<StoresProvider> _l;
	readonly Lazy<DataStorage.Fred.Store> _lazyFredStore;
	readonly Func<ITInvestGateway>? _createGateway;
	readonly TInvestOptions? _startupOptions;
	TInvestStore? _tInvestStore;
	string? _tInvestEnvironment;
	bool _initialized;

	public StoresProvider(StoresOptions opts, ILoggerFactory loggerFactory, TInvestToken? token = null, Func<ITInvestGateway>? gatewayFactory = null)
	{
		if (opts == null) throw new ArgumentNullException(nameof(opts));
		if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));
		_l = loggerFactory.CreateLogger<StoresProvider>();
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
		WatchListTask = MarketDataHub.StartWatchListAsync(tInvestStore);
	}

	public TInvestMarketDataHub? MarketDataHub { get; private set; }
	public string SourcesDescription => _tInvestStore == null ? "FRED" : $"FRED, T-Invest ({_tInvestEnvironment})";
	internal Task WatchListTask { get; private set; } = Task.CompletedTask;

	DataStorage.Fred.Store FredStore => _lazyFredStore.Value;

	// Called after the CLI attaches its logger, before either listener starts.
	public void Initialize()
	{
		if (_initialized || _createGateway == null || _startupOptions == null) return;
		_initialized = true;
		var options = _startupOptions;
		_l.LogInformation("T-Invest source: {environment}, token from {source}; checking access.", options.Environment, options.TokenSource);
		ITInvestGateway? gateway = null;
		try
		{
			gateway = _createGateway();
			TInvestStartup.CheckAsync(gateway, options, DateTimeOffset.UtcNow.AddSeconds(8)).GetAwaiter().GetResult();
			_tInvestStore = new TInvestStore(gateway, options);
			MarketDataHub = new(gateway, options);
			MarketDataHub.Diagnostic += text => _l.LogInformation("T-Invest live: {status}; gRPC {grpcStatus}, code {code}.", text, (gateway as SdkTInvestGateway)?.LastFailureStatus, (gateway as SdkTInvestGateway)?.LastFailureCode);
			MarketDataHub.WatchListRetrying += name => _l.LogWarning("T-Invest watch-list entry is unavailable; retrying in 30 seconds: {symbol}", name);
			WatchListTask = MarketDataHub.StartWatchListAsync(_tInvestStore);
			_tInvestEnvironment = options.Environment.ToLowerInvariant();
			_l.LogInformation("T-Invest source: {environment}, token from {source}; startup check passed.", options.Environment, options.TokenSource);
		}
		catch (Exception ex)
		{
			gateway?.Dispose();
			_l.LogWarning("T-Invest source is not configured: {reason}", ex is TInvestRequestException ? ex.Message : "Startup access check failed.");
		}
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
		if (_tInvestStore == null) return null;
		if (exchange.Length != 0) return (_tInvestStore, TInvestStore.LookupCode(code, exchange));
		var separator = code.LastIndexOf('-');
		if (separator <= 0 || separator == code.Length - 1 || code.StartsWith("fred", StringComparison.OrdinalIgnoreCase)) return null;
		var ticker = code[..separator];
		var classCode = code[(separator + 1)..];
		// Discover a cold class code through an exact lookup, not a guessed ticker-only match.
		// Both successful discovery and misses share the store's cache (negative entries: ten minutes).
		var symbol = await _tInvestStore.ResolveAsync(ticker, classCode, _tInvestStore.Clock.GetUtcNow().AddSeconds(8), cancellationToken);
		return symbol == null ? null : (_tInvestStore, TInvestStore.LookupCode(ticker, classCode));
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
		_l.LogInformation("SearchSymbols({searchText},{searchType},{limit})", searchText, searchType, limit);
		// A leading "fred-" in SearchText is this provider's own routing prefix (see GetSymbolsStoreAsync above),
		// not part of FRED's series ID or title; strip it so a pasted "fred-"-prefixed symbol code still matches.
		var fredPrefix = $"{DataSourceFredPrefix}{DataSourcePrefixSeparator}";
		var normalizedSearchText = searchText.StartsWith(fredPrefix, StringComparison.Ordinal)
			? searchText.Substring(fredPrefix.Length)
			: searchText;
		var result = new List<ISymbol>();
		limit = Math.Clamp(limit, 0, 50);
		if (_tInvestStore != null && !searchText.StartsWith(fredPrefix, StringComparison.Ordinal))
			result.AddRange(await _tInvestStore.SearchAsync(searchText, searchType, limit, exchange, securityType, cancellationToken));
		if (result.Count < limit && exchange.Length == 0 && securityType == SymbolType.Unset)
		{
			var matches = await FredStore.SearchSymbolsAsync(normalizedSearchText, searchType, limit - result.Count, cancellationToken);
			result.AddRange(matches.Select(symbol => (ISymbol)new Symbol(DataSourceFredPrefix, symbol)));
		}
		return result;
	}

	public void Dispose()
	{
		MarketDataHub?.Dispose();
		if (_lazyFredStore.IsValueCreated) _lazyFredStore.Value.Dispose();
		_tInvestStore?.Dispose();
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