namespace SomeDataProvider.DataStorage.TInvest.Test;

using System;
using System.Linq;
using NUnit.Framework;

[TestFixture]
public class MoexCalendarTests
{
	[Test]
	public void SyntheticAdmittedShareGetsTheDatedMorningMainAndEvening()
	{
		var date = new DateOnly(2026, 11, 12);
		var calendar = new MoexCalendarSource(new SyntheticAdmission()).GetCalendar(
			ScriptedTInvestGateway.Invent() with { Market = MoexMarket.Shares }, date, date);
		Assert.That(calendar, Is.Not.Null, "A synthetic fully proven instrument must get its dated windows.");
		Assert.That(calendar!.Windows.Select(w => w.From.UtcDateTime.Hour), Is.EqualTo(new[] { 3, 6, 16 }));
		Assert.That(calendar.Windows.Select(w => w.IsMain), Is.EqualTo(new[] { false, true, false }));
	}

	[TestCase("2026-11-13", "2026-11-13")]
	[TestCase("2026-11-12", "2026-11-13")]
	[TestCase("2026-12-07", "2026-12-07")]
	[TestCase("2026-12-31", "2026-12-31")]
	public void AnnouncedPlansAreUnavailableToCalendarConsumers(string start, string end)
	{
		var source = new MoexCalendarSource(new SyntheticAdmission());
		Assert.That(source.GetCalendar(ScriptedTInvestGateway.Invent() with { Market = MoexMarket.Shares },
			DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture)), Is.Null);
	}

	public sealed class SyntheticAdmission(Func<DateOnly, string, MoexSessionAdmission>? get = null) : IMoexSessionAdmissionSource
	{
		public MoexSessionAdmission GetAdmission(TInvestInstrument instrument, DateOnly civilDate, string session) => get?.Invoke(civilDate, session) ?? new(true, true, "Synthetic dated admission, lifecycle and mode");
	}
}
