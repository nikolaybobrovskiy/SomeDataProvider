namespace SomeDataProvider.DataStorage.TInvest;

public enum TInvestInstrumentKind
{
	Unsupported,
	Share,
	Etf,
	Bond,
	Future,
	Currency,
	StockOption,
	FuturesOption,
	IndexOption,
}

public sealed record TInvestInstrumentShort(string Ticker, string ClassCode, string Uid, string Name, TInvestInstrumentKind Kind, int Lot, bool ApiTradeAvailable);

public sealed record TInvestInstrument(TInvestInstrumentShort Short, decimal Increment, string Currency,
	decimal IncrementAmount = 0, decimal Nominal = 0, decimal BasicAssetSize = 1, DateTimeOffset? Expiration = null, string? Figi = null,
	DateTimeOffset? FirstMinuteCandle = null, DateTimeOffset? FirstDailyCandle = null);

public sealed record TInvestAccess(bool Authenticated, IReadOnlyList<bool> ReadOnlyAccounts);
