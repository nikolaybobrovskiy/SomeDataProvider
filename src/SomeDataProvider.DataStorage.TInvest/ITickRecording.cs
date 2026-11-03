namespace SomeDataProvider.DataStorage.TInvest;

using SomeDataProvider.DataStorage.Definitions;

/// <summary>Trades the bridge recorded itself. Implemented by the stream recorder.</summary>
public interface ITickRecording
{
	/// <summary>UTC intervals of the day during which the instrument was subscribed and live.</summary>
	IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Coverage(string uid, DateOnly day);

	/// <summary>Recorded exchange trades with Start &lt;= time &lt; End, in arrival order.</summary>
	IReadOnlyList<TInvestTrade> Read(string uid, DateOnly day, DateTimeOffset start, DateTimeOffset end);

	/// <summary>Removes recordings of days before the newest archived day.</summary>
	void DeleteBefore(DateOnly newestArchivedDay);
}

/// <summary>One executed trade as history sees it. Source time keeps nanoseconds; DTC records truncate to microseconds.</summary>
public readonly record struct TInvestTick(TInvestTimestamp Time, double Price, long Lots, TInvestDirection Direction);

/// <summary>A tick as a history record: price in every price field, volume = quantity x lot.</summary>
public sealed record TInvestTickRecord(long Microseconds, double LastPrice, double Volume, TInvestDirection Direction) : ISymbolHistoryRecord
{
	static readonly long EpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

	public DateTime TimeStamp => new(EpochTicks + Microseconds * 10, DateTimeKind.Utc);
	public double OpenPrice => LastPrice;
	public double HighPrice => LastPrice;
	public double LowPrice => LastPrice;
}
