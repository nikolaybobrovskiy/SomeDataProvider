namespace SomeDataProvider.DataStorage.TInvest;

public enum TInvestDirection
{
	Unknown,
	Buy,
	Sell
}

public enum TInvestSubscriptionKind
{
	Trades,
	Book,
	Info,
	All
}

public enum TInvestSubscriptionStatus
{
	Unspecified,
	Success,
	InstrumentNotFound,
	SubscriptionActionIsInvalid,
	DepthIsInvalid,
	IntervalIsInvalid,
	LimitIsExceeded,
	InternalError,
	TooManyRequests,
	SubscriptionNotFound,
	SourceIsInvalid,
}

public enum TInvestTradingStatus
{
	Unspecified,
	NotAvailableForTrading,
	OpeningPeriod,
	ClosingPeriod,
	BreakInTrading,
	NormalTrading,
	ClosingAuction,
	DarkPoolAuction,
	DiscreteAuction,
	OpeningAuctionPeriod,
	TradingAtClosingAuctionPrice,
	SessionAssigned,
	SessionClose,
	SessionOpen,
	DealerNormalTrading,
	DealerBreakInTrading,
	DealerNotAvailableForTrading,
	StabilizationAuction,
}

/// <summary>One duplex stream, with one serialized writer and one reader. No SDK objects cross this interface.</summary>
public interface ITInvestMarketDataStream : IDisposable
{
	Task<IReadOnlyList<string>> SendAsync(TInvestStreamRequest request, DateTimeOffset deadline, CancellationToken cancellationToken);
	IAsyncEnumerable<TInvestMarketEvent> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>Source time is kept at nanosecond precision. DTC live messages truncate it to microseconds.</summary>
public readonly record struct TInvestTimestamp(long Seconds, int Nanos)
{
	public long Microseconds => checked(Seconds * 1_000_000 + Nanos / 1_000);
	public DateTimeOffset Utc => DateTimeOffset.FromUnixTimeSeconds(Seconds).AddTicks(Nanos / 100);
}

public abstract record TInvestMarketEvent;
public sealed record TInvestTrade(string Uid, TInvestTimestamp Time, double Price, long Lots, TInvestDirection Direction, bool IsExchange) : TInvestMarketEvent;
public sealed record TInvestQuote(string Uid, TInvestTimestamp Time, double? Bid, long? BidLots, double? Ask, long? AskLots, bool Consistent = true) : TInvestMarketEvent;
public sealed record TInvestStatus(string Uid, TInvestTradingStatus Status) : TInvestMarketEvent;
public sealed record TInvestPing : TInvestMarketEvent;
public sealed record TInvestOtherMessage : TInvestMarketEvent;
public sealed record TInvestSubscriptionElement(string Uid, TInvestSubscriptionStatus Status);
public sealed record TInvestSubscriptionReply(TInvestSubscriptionKind Kind, IReadOnlyList<TInvestSubscriptionElement> Elements) : TInvestMarketEvent;
public sealed record TInvestStreamRequest(TInvestSubscriptionKind Kind, IReadOnlyList<string> Uids, bool Subscribe = true, Func<string, bool>? IsWanted = null);
public sealed record TInvestMarketSnapshot(double? LastPrice = null, TInvestTimestamp? LastTime = null, TInvestQuote? Quote = null, TInvestTradingStatus Status = TInvestTradingStatus.Unspecified, long? LastLots = null);
