namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dtc.Protocol.Private;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;

public partial class ProviderInformationWireTests
{
	[TestCase(MoexMarket.Shares, TInvestInstrumentKind.Share)]
	[TestCase(MoexMarket.Funds, TInvestInstrumentKind.Etf)]
	[TestCase(MoexMarket.Bonds, TInvestInstrumentKind.Bond)]
	[TestCase(MoexMarket.Derivatives, TInvestInstrumentKind.Future)]
	public void ReviewedMoexWindowsAndRevisionReachTheWireWithSyntheticAdmission(MoexMarket market, TInvestInstrumentKind kind)
	{
		var clock = new ManualTimeProvider();
		clock.Advance(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch);
		var source = new MoexCalendarSource(new SyntheticMoexAdmission());
		var date = new DateOnly(2026, 11, 9);
		var instrument = ScriptedTInvestGateway.Invent(kind: kind) with { Market = market };
		var calendar = source.GetCalendar(instrument, date, date)!;
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(instrument with { Calendar = calendar });
		gateway.ResolveCalendar = (id, revision, from, to) => id == calendar.Id && revision == calendar.Revision ? source.GetCalendar(instrument, from, to) : null;
		using var host = new TInvestTestHost(gateway, clock);
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new InstrumentContextRequest { Symbol = "SYNTH-TESTCC" });
		var context = (InstrumentContextResponse)Read(client);
		Assert.That(context.CalendarID, Is.EqualTo(calendar.Id));
		Assert.That(context.CalendarRevision, Is.EqualTo(MoexCalendarSource.Revision));
		Assert.That(context.DailyStampMode, Is.Zero, "Calendar rules do not enable daily acceptance.");
		Send(client, new TradingCalendarRequest { RequestID = 43, CalendarID = context.CalendarID!, CalendarRevision = context.CalendarRevision!, FromTradeDate = 20261109, ToTradeDate = 20261109 });
		var windows = new List<TradingCalendarWindow>();
		ProviderInformationMessage message;
		while ((message = Read(client)) is TradingCalendarWindow window) windows.Add(window);
		Assert.That(message, Is.TypeOf<TradingCalendarEnd>());
		var end = (TradingCalendarEnd)message;
		Assert.That(end.Complete, Is.True);
		Assert.That(end.WindowCount, Is.EqualTo(5), "Two preceding weekend additions and three weekday sessions.");
		Assert.That(end.TradeDateCount, Is.EqualTo(1));
		Assert.That(end.CalendarRevision, Is.EqualTo(MoexCalendarSource.Revision));
		Assert.That(windows.Count, Is.EqualTo(calendar.Windows.Count));
		for (var i = 0; i < windows.Count; i++)
		{
			var actual = windows[i];
			var expected = calendar.Windows[i];
			Assert.That(actual.RequestID, Is.EqualTo(43));
			Assert.That(actual.CalendarID, Is.EqualTo(calendar.Id));
			Assert.That(actual.CalendarRevision, Is.EqualTo(calendar.Revision));
			Assert.That(actual.TradeDate, Is.EqualTo(20261109));
			Assert.That(actual.WindowIndex, Is.EqualTo(i));
			Assert.That(actual.WindowsInDate, Is.EqualTo(5));
			Assert.That(actual.IsMain, Is.EqualTo(expected.IsMain));
			Assert.That(actual.UtcFromMicroseconds, Is.EqualTo((expected.From - DateTimeOffset.UnixEpoch).Ticks / 10));
			Assert.That(actual.UtcToMicroseconds, Is.EqualTo((expected.To - DateTimeOffset.UnixEpoch).Ticks / 10));
			Assert.That(actual.MoscowFrom, Is.EqualTo(expected.From.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)));
			Assert.That(actual.MoscowTo, Is.EqualTo(expected.To.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)));
		}
		Assert.That(gateway.Lookups.Count, Is.EqualTo(1), "Only explicit context resolution looks up an instrument.");
	}

	[TestCase(20250126, 20250126, false, true)]
	[TestCase(20261113, 20261113, false, true)]
	[TestCase(20261112, 20261113, false, true)]
	[TestCase(20261207, 20261207, false, true)]
	[TestCase(20261231, 20261231, false, true)]
	[TestCase(20270101, 20270101, false, true)]
	[TestCase(20250326, 20250326, false, true)]
	[TestCase(20250422, 20250422, false, true)]
	[TestCase(20261109, 20261109, false, false)]
	[TestCase(20261113, 20261113, true, true)]
	[TestCase(20261231, 20261231, true, true)]
	public void MoexUnavailableAndInternalPlansNeverSendWindowsOrComplete(int start, int end, bool internalPreview, bool proven)
	{
		var clock = new ManualTimeProvider();
		var source = proven ? new MoexCalendarSource(new SyntheticMoexAdmission()) : new MoexCalendarSource();
		var instrument = ScriptedTInvestGateway.Invent(kind: TInvestInstrumentKind.Future) with { Market = MoexMarket.Derivatives };
		using var gateway = new ScriptedTInvestGateway();
		gateway.ResolveCalendar = (_, _, from, to) => internalPreview ? source.GetSnapshot(instrument, from, to) : source.GetCalendar(instrument, from, to);
		using var host = new TInvestTestHost(gateway, clock);
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new TradingCalendarRequest { RequestID = 43, CalendarID = MoexCalendarSource.CalendarId(instrument), CalendarRevision = MoexCalendarSource.Revision, FromTradeDate = start, ToTradeDate = end });
		var response = Read(client);
		Assert.That(response, Is.TypeOf<ProviderRequestReject>(), "No plan window or Complete answer can precede the rejection, even for a closed date.");
		Assert.That(((ProviderRequestReject)response).ReasonCode, Is.EqualTo("CalendarUnavailable"));
		Assert.That(response.RequestID, Is.EqualTo(43));
		Assert.That(gateway.Lookups, Is.Empty);
	}

	sealed class SyntheticMoexAdmission : IMoexSessionAdmissionSource
	{
		public MoexSessionAdmission GetAdmission(TInvestInstrument instrument, DateOnly civilDate, string session) => new(true, true, "Synthetic dated admission, lifecycle and mode");
	}
}
