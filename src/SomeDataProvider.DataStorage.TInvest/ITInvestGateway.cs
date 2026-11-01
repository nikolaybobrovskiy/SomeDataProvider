namespace SomeDataProvider.DataStorage.TInvest;

using SomeDataProvider.DataStorage.Definitions;

/// <summary>SDK types never cross this boundary. All methods are read-only.</summary>
public interface ITInvestGateway : IDisposable
{
	Task<TInvestAccess> CheckAccessAsync(DateTimeOffset deadline, CancellationToken cancellationToken);

	Task<TInvestInstrument?> GetInstrumentAsync(string ticker, string classCode, DateTimeOffset deadline, CancellationToken cancellationToken);

	Task<IReadOnlyList<TInvestInstrumentShort>> FindInstrumentsAsync(string query, DateTimeOffset deadline, CancellationToken cancellationToken);

	Task<TInvestRateLimiter.Reservation> ReserveCandlesAsync(int calls, DateTimeOffset deadline, CancellationToken cancellationToken);

	Task<IReadOnlyList<TInvestCandle>> GetCandlesAsync(TInvestCandleRequest request, TInvestRateLimiter.Reservation reservation, DateTimeOffset deadline, CancellationToken cancellationToken);
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
	public bool HasMarketDepth => false; // Enabled when the depth stage lands.
	public bool IsRealTime => true;
	public bool IsDelayed => false;
	public bool IsDiscontinued => false;
	public DataService DataService => DataService.TInvest;
	public string DataServiceSettings => Instrument.Short.Uid;
}
