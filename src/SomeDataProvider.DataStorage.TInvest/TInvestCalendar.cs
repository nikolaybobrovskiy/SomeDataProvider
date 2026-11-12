namespace SomeDataProvider.DataStorage.TInvest;

/// <summary>Only adapters with verified instrument membership and trade-date rules may construct this snapshot.</summary>
public sealed class TInvestCalendar
{
	public TInvestCalendar(string id, string revision, string instrumentUid, string provenance, DateOnly coveredFrom, DateOnly coveredTo,
		DateTimeOffset expires, bool includeDealerTrades, string candleSource, IEnumerable<TInvestCalendarWindow> windows)
	{
		Id = id;
		Revision = revision;
		InstrumentUid = instrumentUid;
		Provenance = provenance;
		CoveredFrom = coveredFrom;
		CoveredTo = coveredTo;
		Expires = expires;
		IncludeDealerTrades = includeDealerTrades;
		CandleSource = candleSource;
		if (new[] { id, revision, instrumentUid, provenance }.Any(string.IsNullOrWhiteSpace) ||
			new[] { id, revision, instrumentUid, provenance }.Any(s => s.Length > 256 || s.Any(char.IsControl)) ||
			coveredTo.DayNumber - coveredFrom.DayNumber is < 0 or > 31 || candleSource is not ("Exchange" or "IncludeWeekend"))
			throw new ArgumentException("A verified calendar requires bounded identity, provenance, source and 1-32 covered dates.");
		var input = windows.Take(513).ToArray();
		if (input.Length > 512) throw new ArgumentException("A calendar snapshot accepts at most 512 input windows.");
		var ordered = input.Distinct().OrderBy(w => w.From).ThenBy(w => w.To).ToArray();
		if (ordered.Any(w => w.TradeDate < coveredFrom || w.TradeDate > coveredTo || w.From >= w.To ||
			w.From.Ticks % TimeSpan.TicksPerSecond != 0 || w.To.Ticks % TimeSpan.TicksPerSecond != 0) ||
			ordered.GroupBy(w => w.TradeDate).Any(g => g.Count() > 16))
			throw new ArgumentException("A calendar needs whole-second forward windows within its covered trade dates, at most 16 per date.");
		for (var i = 1; i < ordered.Length; i++)
			if (ordered[i].From < ordered[i - 1].To) throw new ArgumentException("Calendar windows overlap with conflicting boundaries, ownership or session markers.");
		Windows = Array.AsReadOnly(ordered);
	}

	public string Id { get; }
	public string Revision { get; }
	public string InstrumentUid { get; }
	public string Provenance { get; }
	public DateOnly CoveredFrom { get; }
	public DateOnly CoveredTo { get; }
	public DateTimeOffset Expires { get; }
	public bool IncludeDealerTrades { get; }
	public string CandleSource { get; }
	public IReadOnlyList<TInvestCalendarWindow> Windows { get; }
}
