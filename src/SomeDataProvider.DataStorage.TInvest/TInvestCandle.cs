namespace SomeDataProvider.DataStorage.TInvest;

using SomeDataProvider.DataStorage.Definitions;

public sealed record TInvestCandle(DateTimeOffset Time, double Open, double High, double Low, double Close, long Lots, bool Complete, bool Exchange);

public sealed record TInvestCandleRequest(string Uid, DateTimeOffset Start, DateTimeOffset End, int IntervalSeconds, int Limit, bool IncludeDealer);

public sealed record TInvestCandleInterval(int Seconds, int Limit, TimeSpan? MaximumPeriod = null, int MaximumMonths = 0)
{
	public bool Intraday => Seconds < 86400;
	public bool SecondsOnly => Seconds < 60;
	public DateTimeOffset Advance(DateTimeOffset time) => Seconds == 2592000 ? time.AddMonths(1) : time.AddSeconds(Seconds);
	public DateTimeOffset MaximumEnd(DateTimeOffset start) => MaximumMonths > 0 ? start.AddMonths(MaximumMonths) : start + MaximumPeriod!.Value;

	public static TInvestCandleInterval FromSeconds(int seconds) => seconds switch
	{
		5 => new(5, 2500, TimeSpan.FromMinutes(200)),
		10 => new(10, 1250, TimeSpan.FromMinutes(200)),
		30 => new(30, 2500, TimeSpan.FromHours(20)),
		60 => new(60, 2400, TimeSpan.FromDays(1)),
		120 => new(120, 1200, TimeSpan.FromDays(1)),
		180 => new(180, 750, TimeSpan.FromDays(1)),
		300 => new(300, 2400, TimeSpan.FromDays(7)),
		600 => new(600, 1200, TimeSpan.FromDays(7)),
		900 => new(900, 2400, TimeSpan.FromDays(21)),
		1800 => new(1800, 1200, TimeSpan.FromDays(21)),
		3600 => new(3600, 2400, MaximumMonths: 3),
		7200 => new(7200, 2400, MaximumMonths: 3),
		14400 => new(14400, 700, MaximumMonths: 3),
		86400 => new(86400, 2400, MaximumMonths: 72),
		604800 => new(604800, 300, MaximumMonths: 60),
		2592000 => new(2592000, 120, MaximumMonths: 120),
		_ => throw new TInvestRequestException("Interval not supported by T-Invest.", TInvestFailureKind.Unsupported),
	};
}

public sealed class TInvestCandlePlan
{
	required public TInvestCandleInterval Interval { get; init; }
	required public DateTimeOffset Start { get; init; }
	required public DateTimeOffset End { get; init; }
	required public bool OpenEnded { get; init; }
	required public IReadOnlyList<TInvestCandleRequest> Calls { get; init; }

	public static TInvestCandlePlan Create(TInvestSymbol symbol, int seconds, DateTime start, DateTime end, DateTimeOffset now, TInvestOptions options)
	{
		var interval = TInvestCandleInterval.FromSeconds(seconds);
		static DateTimeOffset Bound(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
		if ((start != default && start.Ticks % TimeSpan.TicksPerSecond != 0) || (end != default && end.Ticks % TimeSpan.TicksPerSecond != 0))
			throw new TInvestRequestException("T-Invest history bounds must be whole UTC seconds.");
		var open = end == default || Bound(end) > now;
		var to = open ? now : Bound(end);
		var first = seconds < 86400 ? symbol.Instrument.FirstMinuteCandle : symbol.Instrument.FirstDailyCandle;
		if (start == default && first == null && !interval.SecondsOnly) throw new TInvestRequestException("T-Invest earliest candle date is unavailable; specify a start time.");
		var retained = now.AddDays(-30);
		// DTC bounds are whole seconds; a derived earliest bound must not precede retention.
		if (retained.Ticks % TimeSpan.TicksPerSecond != 0) retained = DateTimeOffset.FromUnixTimeSeconds(retained.ToUnixTimeSeconds() + 1);
		var earliest = interval.SecondsOnly && (first == null || first < retained) ? retained : first;
		var from = start == default ? earliest!.Value : Bound(start);
		if (to <= from) throw new TInvestRequestException("T-Invest history end must be after start.");
		if (interval.SecondsOnly && from < now.AddDays(-30))
			throw new TInvestRequestException("T-Invest keeps second candles for the last month only.");
		if (first > from) from = first.Value;
		var calls = new List<TInvestCandleRequest>();
		for (var cursor = from; cursor < to;)
		{
			var cap = interval.MaximumEnd(cursor);
			// Intraday calls are split at UTC midnight, even when their API period cap is longer.
			if (interval.Intraday) cap = Min(cap, new DateTimeOffset(cursor.UtcDateTime.Date.AddDays(1), TimeSpan.Zero));
			var until = Min(cap, to);
			// Bound pages before admission, even for a continuously traded instrument.
			var pageEnd = cursor;
			for (var i = 0; i < interval.Limit && pageEnd < until; i++) pageEnd = interval.Advance(pageEnd);
			until = Min(until, pageEnd);
			calls.Add(new(symbol.Instrument.Short.Uid, cursor, until, seconds, interval.Limit, options.IncludeDealerTrades));
			if (calls.Count > options.MaxCandleCallsPerRequest)
				throw new TInvestRequestException($"Range needs more than {options.MaxCandleCallsPerRequest} calls; the limit is {options.MaxCandleCallsPerRequest}; request a shorter range.");
			cursor = until;
		}
		return new() { Interval = interval, Start = from, End = to, OpenEnded = open, Calls = calls };
	}

	static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

sealed record TInvestBar(DateTime TimeStamp, double OpenPrice, double HighPrice, double LowPrice, double LastPrice, double Volume) : ISymbolHistoryRecord;
