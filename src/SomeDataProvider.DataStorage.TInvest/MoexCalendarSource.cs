namespace SomeDataProvider.DataStorage.TInvest;

public sealed class MoexCalendarSource
{
	static readonly MoexRuleTable ReviewedTable = MoexRuleTable.Load();
	readonly MoexRuleTable _table;
	readonly IMoexSessionAdmissionSource _admission;

	public MoexCalendarSource(IMoexSessionAdmissionSource? admission = null)
		: this(ReviewedTable, admission ?? UnprovenAdmission.Instance)
	{
	}

	internal MoexCalendarSource(MoexRuleTable table, IMoexSessionAdmissionSource admission)
	{
		table.Validate();
		_table = table;
		_admission = admission;
	}

	public static string Revision => ReviewedTable.Revision;
	public static string CalendarId(TInvestInstrument instrument) => "moex:" + instrument.Short.Uid;

	public TInvestCalendar? GetCalendar(TInvestInstrument instrument, DateOnly from, DateOnly to) =>
		to > _table.HistoricalTo ? null : GetSnapshot(instrument, from, to);

	// Documentary previews retain plan labels internally; the production seam never returns them.
	internal TInvestCalendar? GetSnapshot(TInvestInstrument instrument, DateOnly from, DateOnly to)
	{
		if (!Enum.IsDefined(instrument.Market) || instrument.Market == MoexMarket.Unknown || from < _table.CoveredFrom || to > _table.CoveredTo ||
			to.DayNumber - from.DayNumber is < 0 or > 31 || _table.UnknownDates.Any(u => u.Market == instrument.Market && u.Date >= from && u.Date <= to)) return null;
		// Even a closed-date answer needs dated instrument evidence; a current flag cannot certify an empty historical calendar.
		for (var date = from; date <= to; date = date.AddDays(1))
		{
			foreach (var session in new[] { "main", "morning", "evening", "weekend" })
				if (!Known(_admission.GetAdmission(instrument, date, session))) return null;
		}
		var windows = new List<TInvestCalendarWindow>();
		foreach (var candidate in _table.Expand(instrument.Market, from, to))
		{
			var session = candidate.Session.StartsWith("main", StringComparison.Ordinal) ? "main" : candidate.Session == "holiday_weekend" ? "weekend" : candidate.Session;
			var admission = _admission.GetAdmission(instrument, candidate.CivilDate, session);
			if (!Known(admission)) return null;
			if (!admission.Admitted) continue;
			windows.Add(candidate.Window with
			{
				Session = candidate.Session,
				Provenance = string.Join(" ", candidate.Sources) + "; admission: " + admission.Provenance,
				IsAnnouncedPlan = candidate.AnnouncedPlan,
			});
		}
		windows = windows.Distinct().ToList();
		if (windows.GroupBy(w => w.TradeDate).Any(g => g.Count() > 16)) return null;
		try
		{
			return new(CalendarId(instrument), _table.Revision, instrument.Short.Uid,
				$"MOEX facts {_table.Revision}; historical through {_table.HistoricalTo:yyyy-MM-dd}; later dates announced plan as of 2026-11-12; dated admission per window",
				from, to, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.FromHours(3)), false, "Exchange", windows)
			{
				RuleMetadata = new(_table.Revision, _table.CoveredFrom, _table.HistoricalTo, _table.CoveredTo, new(2026, 11, 12)),
			};
		}
		catch (ArgumentException) { return null; } // Contradictions and caps fail the whole operation, never a truncated answer.
	}

	static bool Known(MoexSessionAdmission admission) => admission.Proven && !string.IsNullOrWhiteSpace(admission.Provenance) &&
		admission.Provenance.Length <= 256 && !admission.Provenance.Any(char.IsControl);

	sealed class UnprovenAdmission : IMoexSessionAdmissionSource
	{
		internal static readonly UnprovenAdmission Instance = new();
		public MoexSessionAdmission GetAdmission(TInvestInstrument instrument, DateOnly civilDate, string session) =>
			new(false, false, "SDK 0.6.25 has no dated morning/evening admission; a current WeekendFlag is not historical proof.");
	}
}
