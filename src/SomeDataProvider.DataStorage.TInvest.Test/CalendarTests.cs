namespace SomeDataProvider.DataStorage.TInvest.Test;

using System;
using System.Collections.Generic;
using NUnit.Framework;

[TestFixture]
public class CalendarTests
{
	public static readonly DateOnly Friday = new(2026, 11, 6);
	public static readonly DateOnly Monday = new(2026, 11, 9);
	public static readonly DateTimeOffset Expires = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
	public static TInvestCalendarWindow Window(int offset, bool main = false, DateOnly? tradeDate = null) => new(tradeDate ?? Monday,
		new DateTimeOffset(2026, 11, 8, 15, 0, 0, TimeSpan.Zero).AddHours(offset), new DateTimeOffset(2026, 11, 8, 16, 0, 0, TimeSpan.Zero).AddHours(offset), main);
	public static TInvestCalendar Calendar(IEnumerable<TInvestCalendarWindow> windows, DateOnly? from = null, DateOnly? to = null) =>
		new("synthetic-calendar", "synthetic-r1", "invented-uid-SYNTH", "Synthetic dated notice", from ?? Friday, to ?? Monday, Expires, false, "Exchange", windows);

	[TestCase(0)]
	[TestCase(1)]
	[TestCase(2)]
	[TestCase(3)]
	public void InvalidCalendarBoundsAndCapsAreRejected(int variant)
	{
		var windows = new List<TInvestCalendarWindow> { Window(0) };
		if (variant == 0) windows[0] = windows[0] with { To = windows[0].From };
		if (variant == 1) windows[0] = windows[0] with { TradeDate = Monday.AddDays(1) };
		if (variant == 2) for (var i = 1; i < 17; i++) windows.Add(Window(i * 2));
		Assert.Throws<ArgumentException>(() => Calendar(windows, to: variant == 3 ? Friday.AddDays(32) : null));
	}

	[Test]
	public void SnapshotDeduplicatesAndRejectsContradictoryOverlaps()
	{
		var first = Window(0);
		var last = Window(2, true);
		Assert.That(Calendar(new[] { last, first, first }).Windows, Is.EqualTo(new[] { first, last }));
		Assert.Throws<ArgumentException>(() => Calendar(new[] { first, first with { IsMain = true } }));
		Assert.Throws<ArgumentException>(() => Calendar(new[] { first, first with { TradeDate = Friday } }));
	}
}
