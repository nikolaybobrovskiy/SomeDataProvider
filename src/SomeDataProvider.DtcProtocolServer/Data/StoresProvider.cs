namespace SomeDataProvider.DtcProtocolServer.Data;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using NBLib.Logging;

using SomeDataProvider.DataStorage.Definitions;

sealed class StoresProvider : ISymbolHistoryStoreProvider, ISymbolsStoreProvider, IDisposable
{
	public const string DataSourceFredPrefix = "fred";

	public const char DataSourcePrefixSeparator = '-';

	readonly ILogger<StoresProvider> _l;
	readonly DataStorage.Fred.Store _fredStore;

	public StoresProvider(StoresOptions opts, ILoggerFactory loggerFactory)
	{
		if (opts == null) throw new ArgumentNullException(nameof(opts));
		if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));
		// TODO: Create on-demand so we could delete Fred assembly without consequence.
		_fredStore = new DataStorage.Fred.Store(opts.KnownFred.KnownApiKey, loggerFactory);
		_l = loggerFactory.CreateLogger<StoresProvider>();
	}

	public ValueTask<ISymbolHistoryStore> GetSymbolHistoryStoreAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default)
	{
		_l.LogInformation("GetSymbolHistoryStore({symbolCode})", symbol.Code);
		var dataService = symbol.DataService;
		switch (dataService)
		{
			case DataService.Fred:
				return new ValueTask<ISymbolHistoryStore>(_fredStore);
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
				return new ((_fredStore, code.Substring(DataSourceFredPrefix.Length + 1)));
			default:
				return default;
		}
	}

	public async ValueTask<IReadOnlyCollection<ISymbol>> GetKnownSymbolsAsync(CancellationToken cancellationToken = default)
	{
		// TODO: Cache. Log cache (with LogOperation without Async or just LogInformation)?
		return await _l.LogOperationAsync(async () =>
		{
			return (await _fredStore.GetKnownSymbolsAsync(cancellationToken))
				.Select(symbol => new Symbol(DataSourceFredPrefix, symbol)).ToArray();
		}, "GetKnownSymbols()");
	}

	public void Dispose()
	{
		_fredStore.Dispose();
	}

	public class StoresOptions
	{
		public FredOptions? Fred { get; init; }

		public FredOptions KnownFred => Fred ?? throw new InvalidOperationException($"{nameof(Fred)} is not set.");

		public class FredOptions
		{
			public string? ApiKey { get; init; }

			public DataStorage.Fred.ServiceApiKey KnownApiKey => ApiKey != null ? (DataStorage.Fred.ServiceApiKey)ApiKey : throw new InvalidOperationException($"{nameof(Fred)}.{nameof(ApiKey)} is not set.");
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