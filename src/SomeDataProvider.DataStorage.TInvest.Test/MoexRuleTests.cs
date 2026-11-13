namespace SomeDataProvider.DataStorage.TInvest.Test;

using System;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using static MoexCalendarTests;

[TestFixture]
public class MoexRuleTests
{
	[TestCase(MoexMarket.Shares, "2025-03-03", "2025-03-01", "weekend", "14:00-19:00")]
	[TestCase(MoexMarket.Shares, "2025-03-03", "2025-03-02", "weekend", "09:50-19:00")]
	[TestCase(MoexMarket.Funds, "2025-06-30", "2025-06-28", "weekend", "09:50-19:00")]
	[TestCase(MoexMarket.Bonds, "2026-03-30", "2026-03-28", "weekend", "09:50-19:00")]
	[TestCase(MoexMarket.Derivatives, "2025-08-18", "2025-08-16", "weekend", "09:50-19:00")]
	[TestCase(MoexMarket.Derivatives, "2025-01-27", "2025-01-24", "evening", "19:05-23:50")]
	[TestCase(MoexMarket.Derivatives, "2026-03-20", "2026-03-19", "evening", "19:05-23:50")]
	[TestCase(MoexMarket.Derivatives, "2026-03-23", "2026-03-20", "evening", "")]
	[TestCase(MoexMarket.Derivatives, "2026-03-23", "2026-03-23", "evening", "19:00-23:50")]
	[TestCase(MoexMarket.Derivatives, "2026-03-24", "2026-03-23", "evening", "")]
	[TestCase(MoexMarket.Derivatives, "2026-07-13", "2026-07-13", "morning", "08:50-10:00")]
	[TestCase(MoexMarket.Derivatives, "2026-07-14", "2026-07-14", "morning", "06:50-10:00")]
	[TestCase(MoexMarket.Derivatives, "2026-09-11", "2026-09-11", "main", "10:00-19:00")]
	[TestCase(MoexMarket.Derivatives, "2026-09-14", "2026-09-14", "main", "09:00-19:00")]
	[TestCase(MoexMarket.Derivatives, "2026-09-14", "2026-09-14", "morning", "06:50-09:00")]
	[TestCase(MoexMarket.Funds, "2025-02-26", "2025-02-26", "morning", "")]
	[TestCase(MoexMarket.Funds, "2025-02-27", "2025-02-27", "morning", "06:50-09:50")]
	[TestCase(MoexMarket.Bonds, "2025-09-19", "2025-09-19", "morning", "08:50-09:50")]
	[TestCase(MoexMarket.Bonds, "2025-09-22", "2025-09-22", "morning", "06:50-09:50")]
	[TestCase(MoexMarket.Bonds, "2025-06-20", "2025-06-20", "main", "09:50-18:50")]
	[TestCase(MoexMarket.Bonds, "2025-06-23", "2025-06-23", "main", "09:50-19:00")]
	[TestCase(MoexMarket.Shares, "2026-03-20", "2026-03-20", "main", "09:50-18:50")]
	[TestCase(MoexMarket.Shares, "2026-03-23", "2026-03-23", "main", "09:50-19:00")]
	[TestCase(MoexMarket.Funds, "2026-03-23", "2026-03-23", "main", "09:50-19:00")]
	[TestCase(MoexMarket.Shares, "2026-09-14", "2026-09-14", "main", "09:00-19:00")]
	[TestCase(MoexMarket.Bonds, "2026-09-14", "2026-09-14", "main", "09:00-19:00")]
	[TestCase(MoexMarket.Funds, "2026-09-14", "2026-09-14", "morning", "06:50-09:00")]
	[TestCase(MoexMarket.Shares, "2025-02-13", "2025-02-13", "evening", "19:00-20:40")]
	[TestCase(MoexMarket.Bonds, "2025-02-13", "2025-02-13", "evening", "19:00-20:40")]
	[TestCase(MoexMarket.Funds, "2025-02-13", "2025-02-13", "evening", "19:00-20:40")]
	[TestCase(MoexMarket.Derivatives, "2025-04-29", "2025-04-28", "evening", "20:00-23:50")]
	[TestCase(MoexMarket.Derivatives, "2025-08-29", "2025-08-28", "evening", "19:45-23:50")]
	[TestCase(MoexMarket.Derivatives, "2025-11-07", "2025-11-06", "evening", "20:05-23:50")]
	[TestCase(MoexMarket.Derivatives, "2025-09-11", "2025-09-11", "main_after_clearing", "14:05-15:23,15:55-18:50")]
	[TestCase(MoexMarket.Shares, "2025-09-15", "2025-09-13", "weekend", "09:50-10:00,11:40-19:00")]
	[TestCase(MoexMarket.Funds, "2025-09-15", "2025-09-13", "weekend", "09:50-10:00,11:40-19:00")]
	[TestCase(MoexMarket.Derivatives, "2025-09-29", "2025-09-28", "weekend", "10:20-19:00")]
	[TestCase(MoexMarket.Derivatives, "2026-02-24", "2026-02-23", "holiday_weekend", "09:50-19:00")]
	[TestCase(MoexMarket.Derivatives, "2026-05-04", "2026-05-01", "holiday_weekend", "09:50-23:50")]
	[TestCase(MoexMarket.Derivatives, "2026-06-15", "2026-06-12", "holiday_weekend", "09:50-23:50")]
	[TestCase(MoexMarket.Derivatives, "2026-11-05", "2026-11-04", "holiday_weekend", "09:50-23:50")]
	[TestCase(MoexMarket.Bonds, "2026-02-24", "2026-02-23", "holiday_weekend", "")]
	[TestCase(MoexMarket.Bonds, "2026-05-04", "2026-05-01", "holiday_weekend", "09:50-19:00")]
	[TestCase(MoexMarket.Shares, "2026-11-05", "2026-11-04", "holiday_weekend", "09:50-19:00")]
	[TestCase(MoexMarket.Shares, "2026-12-07", "2026-12-05", "weekend", "09:50-19:00")]
	[TestCase(MoexMarket.Funds, "2026-12-07", "2026-12-06", "weekend", "09:50-19:00")]
	[TestCase(MoexMarket.Shares, "2025-01-27", "2025-01-27", "morning", "06:50-09:50")]
	[TestCase(MoexMarket.Derivatives, "2025-01-27", "2025-01-27", "main_before_clearing", "10:00-14:00")]
	[TestCase(MoexMarket.Derivatives, "2025-01-27", "2025-01-27", "main_after_clearing", "14:05-18:50")]
	[TestCase(MoexMarket.Shares, "2025-09-19", "2025-09-19", "evening", "19:00-23:50")]
	[TestCase(MoexMarket.Shares, "2025-09-22", "2025-09-22", "evening", "19:00-23:50")]
	[TestCase(MoexMarket.Funds, "2025-09-22", "2025-09-22", "evening", "19:00-23:50")]
	[TestCase(MoexMarket.Bonds, "2025-09-22", "2025-09-22", "evening", "19:00-23:50")]
	[TestCase(MoexMarket.Derivatives, "2026-03-20", "2026-03-20", "main_after_clearing", "14:05-18:50")]
	[TestCase(MoexMarket.Derivatives, "2026-03-23", "2026-03-23", "main_after_clearing", "")]
	[TestCase(MoexMarket.Derivatives, "2026-03-23", "2026-03-23", "main", "10:00-19:00")]
	[TestCase(MoexMarket.Bonds, "2026-03-23", "2026-03-23", "morning", "06:50-09:50")]
	[TestCase(MoexMarket.Bonds, "2026-07-06", "2026-07-06", "main", "09:50-19:00")]
	[TestCase(MoexMarket.Shares, "2026-09-11", "2026-09-11", "main", "09:50-19:00")]
	[TestCase(MoexMarket.Funds, "2026-09-14", "2026-09-14", "main", "09:00-19:00")]
	[TestCase(MoexMarket.Bonds, "2026-09-11", "2026-09-11", "morning", "06:50-09:50")]
	[TestCase(MoexMarket.Bonds, "2026-09-14", "2026-09-14", "morning", "06:50-09:00")]
	[TestCase(MoexMarket.Derivatives, "2026-11-13", "2026-11-13", "main", "09:00-19:00")]
	public void DatedRulesKeepTheCivilWindowAndOwningDate(MoexMarket market, string owner, string civil, string session, string clocks)
	{
		var calendar = Get(market, D(owner));
		Assert.That(calendar, Is.Not.Null);
		var windows = calendar!.Windows.Where(w => Civil(w) == D(civil) && w.Session == session).ToArray();
		Assert.That(string.Join(",", windows.Select(Clock)), Is.EqualTo(clocks));
		Assert.That(windows.All(w => w.TradeDate == D(owner)), Is.True);
		Assert.That(windows.All(w => w.IsMain == session.StartsWith("main", StringComparison.Ordinal)), Is.True);
		Assert.That(windows.All(w => w.From.Offset == TimeSpan.Zero && w.To.Offset == TimeSpan.Zero), Is.True);
		Assert.That(windows.All(w => w.Provenance!.Contains("https://www.moex.com/", StringComparison.Ordinal)), Is.True);
		Assert.That(windows.All(w => w.Provenance!.Contains("Synthetic dated admission", StringComparison.Ordinal)), Is.True);
	}

	[TestCase(MoexMarket.Shares, "2025-02-22", "2025-02-24")]
	[TestCase(MoexMarket.Funds, "2025-06-14", "2025-06-16")]
	[TestCase(MoexMarket.Bonds, "2026-03-14", "2026-03-16")]
	[TestCase(MoexMarket.Derivatives, "2025-08-09", "2025-08-11")]
	public void BeforeWeekendLaunchTheActualNextMainOwnerHasNoAddition(MoexMarket market, string civil, string owner)
	{
		var table = MoexRuleTable.Load();
		var date = D(civil);
		Assert.That(table.NextMainDate(market, date), Is.EqualTo(D(owner)));
		Assert.That(table.HasException(market, date, "closed_civil_dates") || table.HasException(market, date, "no_weekend_addition"), Is.False,
			"A closure/skip must not hide an erroneously early launch.");
		Assert.That(Get(market, D(owner))!.Windows.Any(w => w.Session == "weekend" && Civil(w) == date), Is.False);
		// Sensitivity control: moving this market's first weekend rule earlier produces the forbidden addition.
		var first = table.Rules.Where(r => r.Market == market && r.Session == "weekend").MinBy(r => r.EffectiveFrom)!;
		var early = table with { Rules = table.Rules.Select(r => r == first ? r with { EffectiveFrom = date } : r).ToArray() };
		var mutant = new MoexCalendarSource(early, new SyntheticAdmission()).GetCalendar(Instrument(market), D(owner), D(owner));
		Assert.That(mutant!.Windows.Any(w => w.Session == "weekend" && Civil(w) == date), Is.True);
	}

	[TestCase(MoexMarket.Shares)]
	[TestCase(MoexMarket.Funds)]
	[TestCase(MoexMarket.Bonds)]
	[TestCase(MoexMarket.Derivatives)]
	public void DisputedWeekdaysWorkingSaturdayClosuresAndSkipsAreDated(MoexMarket market)
	{
		foreach (var date in new[] { "2025-02-24", "2025-03-10", "2025-11-01" })
		{
			var main = Get(market, D(date))!.Windows.Where(w => w.IsMain).ToArray();
			Assert.That(main.Length, Is.EqualTo(market == MoexMarket.Derivatives ? 2 : 1));
			Assert.That(Clock(main[0]).StartsWith(market == MoexMarket.Derivatives ? "10:00" : "09:50", StringComparison.Ordinal), Is.True);
		}
		foreach (var date in new[] { "2025-05-01", "2025-05-09", "2025-12-31", "2026-01-01", "2026-01-02", "2026-01-07", "2026-03-08", "2026-05-09", "2026-12-31" })
			Assert.That(Get(market, D(date))!.Windows, Is.Empty, date);
		var table = MoexRuleTable.Load();
		foreach (var date in table.Exceptions.Where(e => e.Kind == "no_weekend_addition" && e.Markets.Contains(market)).SelectMany(e => e.Dates))
		{
			var owner = table.NextMainDate(market, date);
			if (owner != null) Assert.That(Get(market, owner.Value)!.Windows.Any(w => w.Session == "weekend" && Civil(w) == date), Is.False, date.ToString());
		}
	}

	[TestCase("2025-03-26")]
	[TestCase("2025-04-22")]
	[TestCase("2025-01-26")]
	[TestCase("2027-01-01")]
	public void UnknownDatesNeverBorrowNormalWindows(string date) => Assert.That(Get(MoexMarket.Derivatives, D(date)), Is.Null);

	[TestCase("morning")]
	[TestCase("evening")]
	[TestCase("weekend")]
	[TestCase("main")]
	public void MissingDatedAdmissionMakesTheWholeAnswerUnknown(string missing)
	{
		var source = new MoexCalendarSource(new SyntheticAdmission((_, session) => new(session != missing, true, "Synthetic dated admission")));
		Assert.That(source.GetCalendar(Instrument(MoexMarket.Shares), D("2026-11-09"), D("2026-11-09")), Is.Null);
		Assert.That(new MoexCalendarSource().GetCalendar(Instrument(MoexMarket.Shares), D("2026-12-31"), D("2026-12-31")), Is.Null);
	}

	[Test]
	public void ProvenDenialUnknownPrecedingWeekendAndMissingProvenanceAreDifferent()
	{
		var instrument = Instrument(MoexMarket.Shares);
		var date = D("2026-11-09");
		var denied = new MoexCalendarSource(new SyntheticAdmission((_, session) => new(true, session != "weekend", "Synthetic dated admission"))).GetCalendar(instrument, date, date)!;
		Assert.That(denied.Windows.Count, Is.EqualTo(3));
		Assert.That(new MoexCalendarSource(new SyntheticAdmission((civil, _) => new(civil >= date, true, "Synthetic dated admission"))).GetCalendar(instrument, date, date), Is.Null);
		Assert.That(Get(MoexMarket.Unknown, date), Is.Null);
		Assert.That(Get((MoexMarket)99, date), Is.Null);
		foreach (var provenance in new[] { string.Empty, "\n", new string('a', 257) })
			Assert.That(new MoexCalendarSource(new SyntheticAdmission((_, _) => new(true, true, provenance))).GetCalendar(instrument, date, date), Is.Null);
	}

	[Test]
	public void PlannedDatesKeepTheirLabelAndTheHalfOpenMinuteBoundary()
	{
		var calendar = Get(MoexMarket.Shares, D("2026-11-13"))!;
		Assert.That(calendar.Windows.All(w => w.IsAnnouncedPlan), Is.True);
		Assert.That(calendar.Provenance, Does.Contain("announced plan as of 2026-11-12"));
		Assert.That(calendar.RuleMetadata, Is.EqualTo(new MoexCalendarMetadata(calendar.Revision,
			D("2025-01-27"), D("2026-11-12"), D("2026-12-31"), D("2026-11-12"))));
		Assert.That(calendar.CoveredFrom, Is.EqualTo(D("2026-11-13")));
		Assert.That(calendar.CoveredTo, Is.EqualTo(D("2026-11-13")));
		Assert.That(calendar.Expires, Is.EqualTo(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.FromHours(3))));
		Assert.That(calendar.Windows.Last().To, Is.EqualTo(new DateTimeOffset(2026, 11, 13, 20, 50, 0, TimeSpan.Zero)));
		Assert.That(Get(MoexMarket.Shares, D("2026-11-12"))!.Windows.Any(w => w.IsAnnouncedPlan), Is.False);
	}

	[Test]
	public void ReleasedTableAndEveryCoveredMonthHaveConsistentBounds()
	{
		var table = MoexRuleTable.Load();
		Assert.DoesNotThrow(table.Validate);
		Assert.That(table.Rules.Length, Is.EqualTo(71));
		Assert.That(table.Rules.Count(r => r.AnnouncedPlan), Is.EqualTo(16));
		Assert.That(table.Rules.Count(r => r.BoundarySupport), Is.EqualTo(1));
		foreach (var market in new[] { MoexMarket.Shares, MoexMarket.Funds, MoexMarket.Bonds, MoexMarket.Derivatives })
		{
			for (var from = table.CoveredFrom; from <= table.CoveredTo; from = from.AddDays(32))
			{
				var to = from.AddDays(31) > table.CoveredTo ? table.CoveredTo : from.AddDays(31);
				if (table.UnknownDates.Any(u => u.Market == market && u.Date >= from && u.Date <= to)) continue;
				var calendar = new MoexCalendarSource(new SyntheticAdmission()).GetSnapshot(Instrument(market), from, to);
				Assert.That(calendar, Is.Not.Null, $"{market} {from}");
				Assert.That(calendar!.Windows.GroupBy(w => w.TradeDate).All(g => g.Count() <= 16), Is.True);
			}
		}
	}

	[TestCase("source")]
	[TestCase("overlap")]
	[TestCase("coverage")]
	[TestCase("plan")]
	public void CorruptRuleFactsAreRejected(string variant)
	{
		var table = MoexRuleTable.Load();
		var rule = table.Rules[1];
		table = variant switch
		{
			"source" => table with { Rules = new[] { rule with { Sources = Array.Empty<string>() } } },
			"overlap" => table with { Rules = new[] { rule, rule } },
			"coverage" => table with { Rules = new[] { rule with { EffectiveTo = D("2027-01-01") } } },
			_ => table with { Rules = new[] { rule with { AnnouncedPlan = true } } },
		};
		Assert.Throws<InvalidOperationException>(table.Validate);
	}

	[TestCase(16, true)]
	[TestCase(17, false)]
	public void WindowCapNeverTruncates(int count, bool available)
	{
		var table = MoexRuleTable.Load();
		var day = D("2026-11-12");
		var rule = table.Rules.First(r => r.Market == MoexMarket.Shares && r.Session == "main" && r.EffectiveFrom <= day && r.EffectiveTo >= day);
		var intervals = Enumerable.Range(0, count).Select(i => new MoexInterval(new TimeOnly(9, 0).AddMinutes(i * 2), new TimeOnly(9, 1).AddMinutes(i * 2))).ToArray();
		table = table with { Rules = new[] { rule }, Overrides = new[] { new MoexOverride(MoexMarket.Shares, "main", day, intervals, rule.Sources) } };
		var calendar = new MoexCalendarSource(table, new SyntheticAdmission()).GetCalendar(Instrument(MoexMarket.Shares), day, day);
		Assert.That(calendar != null, Is.EqualTo(available));
		if (available) Assert.That(calendar!.Windows.Count, Is.EqualTo(count));
	}

	static DateOnly D(string date) => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
	static TInvestInstrument Instrument(MoexMarket market) => ScriptedTInvestGateway.Invent(kind: market switch
	{
		MoexMarket.Funds => TInvestInstrumentKind.Etf, MoexMarket.Bonds => TInvestInstrumentKind.Bond,
		MoexMarket.Derivatives => TInvestInstrumentKind.Future, _ => TInvestInstrumentKind.Share,
	}) with { Market = market };
	static TInvestCalendar? Get(MoexMarket market, DateOnly date) => new MoexCalendarSource(new SyntheticAdmission()).GetSnapshot(Instrument(market), date, date);
	static DateOnly Civil(TInvestCalendarWindow window) => DateOnly.FromDateTime(window.From.ToOffset(TimeSpan.FromHours(3)).DateTime);
	static string Clock(TInvestCalendarWindow window) => window.From.ToOffset(TimeSpan.FromHours(3)).ToString("HH:mm", CultureInfo.InvariantCulture) + "-" + window.To.ToOffset(TimeSpan.FromHours(3)).ToString("HH:mm", CultureInfo.InvariantCulture);
}
