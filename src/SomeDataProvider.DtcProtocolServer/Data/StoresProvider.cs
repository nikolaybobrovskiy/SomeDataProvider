namespace SomeDataProvider.DtcProtocolServer.Data;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using NBLib.Logging;

using SomeDataProvider.DataStorage.Definitions;

sealed partial class StoresProvider : ISymbolHistoryStoreProvider, ISymbolsStoreProvider, IDisposable
{
	public const string DataSourceFredPrefix = "fred";

	public const char DataSourcePrefixSeparator = '-';

	readonly ILogger<StoresProvider> _l;
	readonly Lazy<DataStorage.Fred.Store> _lazyFredStore;

	public StoresProvider(StoresOptions opts, ILoggerFactory loggerFactory)
	{
		if (opts == null) throw new ArgumentNullException(nameof(opts));
		if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));
		_l = loggerFactory.CreateLogger<StoresProvider>();
		_lazyFredStore = new (() => new DataStorage.Fred.Store(opts.KnownFred.KnownApiKey, loggerFactory));
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

	DataStorage.Fred.Store FredStore => _lazyFredStore.Value;

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

	public ValueTask<(ISymbolsStore SymbolsStore, string SymbolCode)?> GetSymbolsStoreAsync(string code, CancellationToken cancellationToken = default)
	{
		_l.LogInformation("GetSymbolsStore({symbolCode})", code);
		switch (code)
		{
			case var _ when code.StartsWith($"{DataSourceFredPrefix}{DataSourcePrefixSeparator}", StringComparison.Ordinal):
				return new ((FredStore, code.Substring(DataSourceFredPrefix.Length + 1)));
			default:
				return default;
		}
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
	{
		_l.LogInformation("SearchSymbols({searchText},{searchType},{limit})", searchText, searchType, limit);
		// A leading "fred-" in SearchText is this provider's own routing prefix (see GetSymbolsStoreAsync above),
		// not part of FRED's series ID or title; strip it so a pasted "fred-"-prefixed symbol code still matches.
		var fredPrefix = $"{DataSourceFredPrefix}{DataSourcePrefixSeparator}";
		var normalizedSearchText = searchText.StartsWith(fredPrefix, StringComparison.Ordinal)
			? searchText.Substring(fredPrefix.Length)
			: searchText;
		var matches = await FredStore.SearchSymbolsAsync(normalizedSearchText, searchType, limit, cancellationToken);
		return matches.Select(symbol => (ISymbol)new Symbol(DataSourceFredPrefix, symbol)).ToArray();
	}

	public void Dispose()
	{
		if (_lazyFredStore.IsValueCreated) _lazyFredStore.Value.Dispose();
	}

	public class StoresOptions
	{
		public FredOptions? Fred { get; init; }

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