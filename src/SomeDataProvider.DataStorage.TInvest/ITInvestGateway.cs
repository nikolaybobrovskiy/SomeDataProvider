namespace SomeDataProvider.DataStorage.TInvest;

using SomeDataProvider.DataStorage.Definitions;

/// <summary>SDK types never cross this boundary. All methods are read-only.</summary>
public interface ITInvestGateway : IDisposable
{
	/// <summary>True only when each recent source attempt invokes the dispatch guard after source admission.</summary>
	bool SupportsRecentClosedTicks => false;
	bool SupportsCatalog => false;

	/// <summary>Returns only a verified instrument calendar and trade-date mapping, never inferred venue hours.</summary>
	Task<TInvestCalendar?> GetTradingCalendarAsync(string id, string revision, DateOnly from, DateOnly to, DateTimeOffset deadline, CancellationToken cancellationToken) => Task.FromResult<TInvestCalendar?>(null);

	Task<TInvestAccess> CheckAccessAsync(DateTimeOffset deadline, CancellationToken cancellationToken);

	Task<TInvestInstrument?> GetInstrumentAsync(string ticker, string classCode, DateTimeOffset deadline, CancellationToken cancellationToken);

	Task<IReadOnlyList<TInvestInstrument>> ListInstrumentsAsync(TInvestInstrumentKind kind, DateTimeOffset deadline, CancellationToken cancellationToken) =>
		throw new TInvestRequestException("Bulk catalogs are not supported by this gateway.", TInvestFailureKind.Unsupported);

	Task<IReadOnlyList<TInvestInstrumentShort>> FindInstrumentsAsync(string query, DateTimeOffset deadline, CancellationToken cancellationToken);

	Task<ITInvestMarketDataStream> OpenMarketDataStreamAsync(CancellationToken cancellationToken);

	Task<TInvestMarketSnapshot> GetMarketSnapshotAsync(string uid, bool includeDealer, DateTimeOffset deadline, CancellationToken cancellationToken);

	Task<TInvestOrderBook> GetOrderBookAsync(string uid, int depth, DateTimeOffset deadline, CancellationToken cancellationToken);

	Task<TInvestRateLimiter.Reservation> ReserveCandlesAsync(int calls, DateTimeOffset deadline, CancellationToken cancellationToken);

	Task<IReadOnlyList<TInvestCandle>> GetCandlesAsync(TInvestCandleRequest request, TInvestRateLimiter.Reservation reservation, DateTimeOffset deadline, CancellationToken cancellationToken);

	/// <summary>Books archive downloads in the shared archive window (30 a minute per address, 90 % used).</summary>
	Task<TInvestRateLimiter.Reservation> ReserveArchivesAsync(int downloads, DateTimeOffset deadline, CancellationToken cancellationToken);

	/// <summary>Downloads one instrument-day archive to <paramref name="destination"/>; false when the host has none (HTTP 404).</summary>
	Task<bool> DownloadTradeArchiveAsync(string instrumentId, DateOnly day, string destination, TInvestRateLimiter.Reservation? reservation, DateTimeOffset deadline, CancellationToken cancellationToken);

	/// <summary>Exact closed exchange-only source range. No hidden calls; invoke beforeDispatch immediately before every attempt.</summary>
	Task<IReadOnlyList<TInvestTrade>> GetRecentTradesAsync(string uid, DateTimeOffset from, DateTimeOffset to, int maximumAttempts, Action beforeDispatch, DateTimeOffset deadline, CancellationToken cancellationToken) =>
		throw new TInvestRequestException("Recent closed ticks are not supported by this gateway.", TInvestFailureKind.Unsupported);

	/// <summary>The last hour's public exchange trades. The caller filters.</summary>
	Task<IReadOnlyList<TInvestTrade>> GetLastTradesAsync(string uid, DateTimeOffset from, DateTimeOffset to, TInvestRateLimiter.Reservation reservation, DateTimeOffset deadline, CancellationToken cancellationToken);
}

public sealed class TInvestSymbol : ISymbol
{
	public TInvestSymbol(TInvestInstrument instrument) => Instrument = instrument;

	public TInvestInstrument Instrument { get; }

	public string Code => Instrument.Short.Ticker;
	public string Exchange => Instrument.Short.ClassCode;
	public SymbolType Type => Instrument.Short.Kind switch
	{
		TInvestInstrumentKind.Share or TInvestInstrumentKind.Etf => SymbolType.Stock,
		TInvestInstrumentKind.Bond => SymbolType.Bond,
		TInvestInstrumentKind.Future => SymbolType.Futures,
		TInvestInstrumentKind.Currency => SymbolType.Forex,
		TInvestInstrumentKind.StockOption => SymbolType.StockOption,
		TInvestInstrumentKind.FuturesOption => SymbolType.FuturesOption,
		TInvestInstrumentKind.IndexOption => SymbolType.IndexOption,
		_ => SymbolType.Unset,
	};
	public string Description => $"{Instrument.Short.Name} (lot {Instrument.Short.Lot})";
	public string? Category => null;
	public int NumberOfDecimals
	{
		get
		{
			var increment = Instrument.Increment;
			var decimals = 0;
			while (increment != decimal.Truncate(increment) && decimals < 9)
			{
				increment *= 10;
				decimals++;
			}
			return decimals;
		}
	}
	public float MinPriceIncrement => (float)Instrument.Increment;
	public string Currency => Instrument.Currency.ToUpperInvariant();
	public double CurrencyValuePerIncrement => (double)(Instrument.Short.Kind switch
	{
		TInvestInstrumentKind.Future => Instrument.IncrementAmount,
		TInvestInstrumentKind.Bond => Instrument.Increment * Instrument.Nominal / 100,
		_ => Instrument.Increment,
	});
	public double ContractSize => Instrument.Short.Kind == TInvestInstrumentKind.Future ? (double)Instrument.BasicAssetSize : 1;
	public DateTime? SecurityExpirationDate => Instrument.Expiration?.UtcDateTime;
	public string ExchangeSymbol => $"{Code}_{Exchange}";
	public bool HasMarketDepth => Instrument.Short.Kind is not (TInvestInstrumentKind.StockOption or TInvestInstrumentKind.FuturesOption or TInvestInstrumentKind.IndexOption);
	public bool IsRealTime => true;
	public bool IsDelayed => false;
	public bool IsDiscontinued => false;
	public DataService DataService => DataService.TInvest;
	public string DataServiceSettings => Instrument.Short.Uid;
}
