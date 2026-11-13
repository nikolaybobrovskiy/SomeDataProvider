namespace SomeDataProvider.DataStorage.TInvest;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Facts and source links only. Date ranges are inclusive Moscow civil dates, not owning trade dates.</summary>
internal sealed record MoexRuleTable(string Revision, DateOnly CoveredFrom, DateOnly HistoricalTo, DateOnly CoveredTo,
	DateOnly BoundarySupportFrom, MoexRule[] Rules, MoexException[] Exceptions, MoexOverride[] Overrides, MoexUnknownDate[] UnknownDates)
{
	internal static readonly string[] Sessions = new[] { "morning", "main", "main_before_clearing", "main_after_clearing", "evening", "weekend", "holiday_weekend" };

	internal static MoexRuleTable Load()
	{
		using var stream = typeof(MoexRuleTable).Assembly.GetManifestResourceStream("SomeDataProvider.DataStorage.TInvest.Calendars.moex-session-rules.json")
			?? throw new InvalidOperationException("The reviewed MOEX rule resource is missing.");
		var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
		options.Converters.Add(new JsonStringEnumConverter());
		var table = JsonSerializer.Deserialize<MoexRuleTable>(stream, options) ?? throw new InvalidOperationException("The MOEX rule resource is empty.");
		table.Validate();
		return table;
	}

	internal void Validate()
	{
		static bool Sources(string[] urls) => urls.Length > 0 && urls.All(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) &&
			uri.Scheme == "https" && uri.Host is "moex.com" or "www.moex.com");
		if (string.IsNullOrWhiteSpace(Revision) || CoveredFrom > HistoricalTo || HistoricalTo >= CoveredTo || BoundarySupportFrom > CoveredFrom || Rules.Length == 0)
			throw new InvalidOperationException("Invalid MOEX revision or coverage.");
		foreach (var rule in Rules)
		{
			if (rule.Market == MoexMarket.Unknown || rule.EffectiveFrom > rule.EffectiveTo || rule.EffectiveTo > CoveredTo ||
				(rule.EffectiveFrom < CoveredFrom && !(rule.BoundarySupport && rule.Market == MoexMarket.Derivatives && rule.Session == "evening" &&
					rule.NextMain && rule.EffectiveFrom == BoundarySupportFrom && rule.EffectiveTo == BoundarySupportFrom)) ||
				rule.AnnouncedPlan != (rule.EffectiveFrom > HistoricalTo) || (!rule.AnnouncedPlan && rule.EffectiveTo > HistoricalTo) ||
				rule.Weekdays is not ("Mon-Fri;working_day_override" or "Sat;Sun" or "Sat" or "designated_holiday") ||
				!Sessions.Contains(rule.Session) || rule.Start >= rule.End || !Sources(rule.Sources))
				throw new InvalidOperationException("Invalid MOEX rule, clock, source or civil-date coverage.");
		}
		foreach (var group in Rules.GroupBy(r => (r.Market, r.Session)))
		{
			var rows = group.OrderBy(r => r.EffectiveFrom).ToArray();
			for (var i = 1; i < rows.Length; i++)
				if (rows[i].EffectiveFrom <= rows[i - 1].EffectiveTo) throw new InvalidOperationException("MOEX rules overlap for the same market/session key.");
		}
		if (Exceptions.Any(e => e.Markets.Length == 0 || e.Dates.Length == 0 || e.Markets.Contains(MoexMarket.Unknown) ||
			e.Dates.Any(d => d < CoveredFrom || d > CoveredTo) || !Sources(e.Sources)) ||
			Overrides.Any(o => o.Market == MoexMarket.Unknown || !Sessions.Contains(o.Session) || o.Date < CoveredFrom || o.Date > CoveredTo ||
				o.Intervals.Any(i => i.Start >= i.End) || !Sources(o.Sources)) ||
			Overrides.GroupBy(o => (o.Market, o.Session, o.Date)).Any(g => g.Count() != 1) ||
			UnknownDates.Any(u => u.Date < CoveredFrom || u.Date > CoveredTo || !Sources(u.Sources)))
			throw new InvalidOperationException("Invalid MOEX exception or source.");
	}

	internal bool HasException(MoexMarket market, DateOnly date, string kind) =>
		Exceptions.Any(e => e.Kind == kind && e.Markets.Contains(market) && e.Dates.Contains(date));

	internal bool IsMainDate(MoexMarket market, DateOnly date) => date >= CoveredFrom && date <= CoveredTo &&
		(date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) || HasException(market, date, "normal_working_saturday")) &&
		!HasException(market, date, "closed_civil_dates") && !HasException(market, date, "designated_holiday_addition") &&
		!HasException(market, date, "designated_holiday_no_launched_addition");

	internal DateOnly? NextMainDate(MoexMarket market, DateOnly date)
	{
		for (var next = date.AddDays(1); next <= CoveredTo; next = next.AddDays(1))
			if (IsMainDate(market, next)) return next;
		return null;
	}

	internal IEnumerable<MoexCandidate> Expand(MoexMarket market, DateOnly from, DateOnly to)
	{
		foreach (var rule in Rules.Where(r => r.Market == market))
		{
			for (var civil = rule.EffectiveFrom; civil <= rule.EffectiveTo && civil <= to; civil = civil.AddDays(1))
			{
				var main = rule.BoundarySupport || IsMainDate(market, civil);
				var active = rule.Session switch
				{
					"holiday_weekend" => HasException(market, civil, "designated_holiday_addition"),
					"weekend" => civil.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && !main &&
						!HasException(market, civil, "closed_civil_dates") && !HasException(market, civil, "no_weekend_addition"),
					_ => main,
				};
				if (!active || (rule.Session == "evening" && HasException(market, civil, "no_evening"))) continue;
				var owner = rule.NextMain ? NextMainDate(market, civil) : civil;
				if (owner == null || owner < from || owner > to) continue;
				var replacement = Overrides.SingleOrDefault(o => o.Market == market && o.Session == rule.Session && o.Date == civil);
				foreach (var interval in replacement?.Intervals ?? new[] { new MoexInterval(rule.Start, rule.End) })
				{
					yield return new(rule.Session, civil, new(owner.Value, Utc(civil, interval.Start), Utc(civil, interval.End), rule.Session.StartsWith("main", StringComparison.Ordinal)),
						rule.AnnouncedPlan, (replacement?.Sources ?? rule.Sources).Concat(Exceptions.Where(e => e.Markets.Contains(market) && e.Dates.Contains(civil)).SelectMany(e => e.Sources)).Distinct().Order().ToArray());
				}
			}
		}
	}

	static DateTimeOffset Utc(DateOnly date, TimeOnly time) => new DateTimeOffset(date.ToDateTime(time), TimeSpan.FromHours(3)).ToUniversalTime();
}

internal sealed record MoexRule(MoexMarket Market, string Session, DateOnly EffectiveFrom, DateOnly EffectiveTo, string Weekdays,
	TimeOnly Start, TimeOnly End, bool NextMain, bool AnnouncedPlan, bool BoundarySupport, string[] Sources);
internal sealed record MoexException(string Kind, DateOnly[] Dates, MoexMarket[] Markets, string[] Sources);
internal sealed record MoexInterval(TimeOnly Start, TimeOnly End);
internal sealed record MoexOverride(MoexMarket Market, string Session, DateOnly Date, MoexInterval[] Intervals, string[] Sources);
internal sealed record MoexUnknownDate(MoexMarket Market, DateOnly Date, string[] Sources);
internal sealed record MoexCandidate(string Session, DateOnly CivilDate, TInvestCalendarWindow Window, bool AnnouncedPlan, string[] Sources);
