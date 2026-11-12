namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Dtc.Protocol.Private;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;

[TestFixture]
[NonParallelizable]
public partial class ProviderInformationWireTests
{
	[TestCase(false)]
	[TestCase(true)]
	public void HelloReportsTheClockAndEnablesOnlyTheImplementedProofBit(bool historyOnly)
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, clock, onlyHistoryServer: historyOnly);
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { RequestID = 41, BudgetScopeID = "synthetic-generation" });
		var response = (ProviderHelloResponse)Read(client);
		Assert.That(response.RequestID, Is.EqualTo(41));
		Assert.That(response.SelectedVersion, Is.EqualTo(1));
		Assert.That(response.Features & ProviderInformationFeatures.EmptyDayProofV1, Is.Not.Zero);
		Assert.That(response.Features & ProviderInformationFeatures.RecentClosedTicksV1, Is.Not.Zero);
		Assert.That(response.ProviderUtcMicroseconds, Is.EqualTo((clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks / 10));
		Assert.That(response.BudgetScopeID, Is.EqualTo("synthetic-generation"));
		Assert.That(response.MaxCalendarDates, Is.EqualTo(32));
		Assert.That(response.MaxWindowsPerDate, Is.EqualTo(16));
		Assert.That(gateway.Lookups, Is.Empty);
	}

	[Test]
	public void ContextUsesGatewayIdentityAndActualStoreCaps()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var host = new TInvestTestHost(gateway, clock, new() { MaxTickDaysPerRequest = 3, MaxTicksPerRequest = 25000 });
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new InstrumentContextRequest { RequestID = 42, Symbol = "SYNTH-TESTCC" });
		var message = Read(client);
		Assert.That(message, Is.TypeOf<InstrumentContextResponse>());
		var response = (InstrumentContextResponse)message;
		Assert.That(response.RequestID, Is.EqualTo(42));
		Assert.That(response.Source, Is.EqualTo("t-invest"));
		Assert.That(response.Kind, Is.EqualTo(1));
		Assert.That(response.InstrumentUID, Is.EqualTo(gateway.Instruments[0].Short.Uid));
		Assert.That(response.Lot, Is.EqualTo(10));
		Assert.That(response.ExactIncrement, Is.EqualTo("0.01"));
		Assert.That(response.MaxTickDaysPerRequest, Is.EqualTo(3));
		Assert.That(response.MaxTicksPerRequest, Is.EqualTo(25000));
		Assert.That(response.UIDEpoch, Is.Not.Empty);
		Assert.That(response.DailyStampMode, Is.Zero);
		Assert.That(response.CalendarID, Is.Null);
		Assert.That(response.CalendarRevision, Is.Null);
	}

	[Test]
	public void CalendarRetainsWeekendAndEveningWindowsOwnedByLaterTradeDate()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Calendar = CalendarTests.Calendar(new[] { CalendarTests.Window(0), CalendarTests.Window(18, true), CalendarTests.Window(0) });
		using var host = new TInvestTestHost(gateway, clock);
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new TradingCalendarRequest { RequestID = 43, CalendarID = "synthetic-calendar", CalendarRevision = "synthetic-r1", FromTradeDate = 20261106, ToTradeDate = 20261109 });
		var message = Read(client);
		Assert.That(message, Is.TypeOf<TradingCalendarWindow>());
		var first = (TradingCalendarWindow)message;
		Assert.That(first.TradeDate, Is.EqualTo(20261109));
		Assert.That(first.MoscowFrom, Is.EqualTo("2026-11-08T18:00:00"));
		Assert.That(first.WindowIndex, Is.Zero);
		Assert.That(first.WindowsInDate, Is.EqualTo(2));
		Assert.That(first.IsMain, Is.False);
		var second = (TradingCalendarWindow)Read(client);
		Assert.That(second.WindowIndex, Is.EqualTo(1));
		Assert.That(second.IsMain, Is.True);
		var end = (TradingCalendarEnd)Read(client);
		Assert.That(end.Complete, Is.True);
		Assert.That(end.TradeDateCount, Is.EqualTo(4));
		Assert.That(end.WindowCount, Is.EqualTo(2));
	}

	[Test]
	public void ContextCarriesAllKnownOptionalFieldsAndChangesEpochWithDefinition()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		var instrument = ScriptedTInvestGateway.Invent() with
		{
			Board = "SYNTHBOARD", BoardName = "Synthetic board", PriceUnit = "currency", DailyStampMode = 1,
			Calendar = CalendarTests.Calendar(Array.Empty<SomeDataProvider.DataStorage.TInvest.TInvestCalendarWindow>()),
			FirstMinuteCandle = new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero), FirstDailyCandle = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero),
		};
		gateway.Resolve = (_, _, _) => System.Threading.Tasks.Task.FromResult<SomeDataProvider.DataStorage.TInvest.TInvestInstrument?>(instrument);
		using var host = new TInvestTestHost(gateway, clock);
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		InstrumentContextResponse Context()
		{
			Send(client, new InstrumentContextRequest { RequestID = 44, Symbol = "SYNTH-TESTCC" });
			return (InstrumentContextResponse)Read(client);
		}
		var first = Context();
		Assert.That(first.Board, Is.EqualTo("SYNTHBOARD"));
		Assert.That(first.BoardName, Is.EqualTo("Synthetic board"));
		Assert.That(first.PriceUnit, Is.EqualTo("currency"));
		Assert.That(first.Currency, Is.EqualTo("ZZZ"));
		Assert.That(first.MonetaryTickValue, Is.EqualTo("0.01"));
		Assert.That(first.FirstMinuteDate, Is.EqualTo(20250401));
		Assert.That(first.FirstDailyDate, Is.EqualTo(20250301));
		Assert.That(first.CalendarID, Is.EqualTo("synthetic-calendar"));
		Assert.That(first.CalendarRevision, Is.EqualTo("synthetic-r1"));
		Assert.That(first.DailyStampMode, Is.EqualTo(1));
		Assert.That(first.Description, Is.EqualTo("Invented instrument"));
		Assert.That(Context().UIDEpoch, Is.EqualTo(first.UIDEpoch));
		instrument = instrument with { Increment = 0.05m };
		Assert.That(Context().UIDEpoch, Is.Not.EqualTo(first.UIDEpoch));
		instrument = instrument with { Short = instrument.Short with { Uid = "invented-other-uid" } };
		Assert.That(Context().InstrumentUID, Is.EqualTo("invented-other-uid"));
		Assert.That(Context().CalendarID, Is.Null, "A calendar for the old UID must not leak into a new identity.");
	}

	[Test]
	public void CalendarAtBothWireCapsKeepsEveryWindow()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		var windows = System.Linq.Enumerable.Range(0, 16).Select(i => new SomeDataProvider.DataStorage.TInvest.TInvestCalendarWindow(CalendarTests.Monday,
			new DateTimeOffset(2026, 11, 9, 6, 0, 0, TimeSpan.Zero).AddMinutes(i * 30), new DateTimeOffset(2026, 11, 9, 6, 15, 0, TimeSpan.Zero).AddMinutes(i * 30), i == 0));
		gateway.Calendar = CalendarTests.Calendar(windows, CalendarTests.Friday, CalendarTests.Friday.AddDays(31));
		using var host = new TInvestTestHost(gateway, clock);
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new TradingCalendarRequest { CalendarID = "synthetic-calendar", CalendarRevision = "synthetic-r1", FromTradeDate = 20261106, ToTradeDate = 20261207 });
		for (var i = 0; i < 16; i++)
		{
			var window = (TradingCalendarWindow)Read(client);
			Assert.That(window.WindowIndex, Is.EqualTo(i));
			Assert.That(window.WindowsInDate, Is.EqualTo(16));
		}
		var end = (TradingCalendarEnd)Read(client);
		Assert.That(end.TradeDateCount, Is.EqualTo(32));
		Assert.That(end.WindowCount, Is.EqualTo(16));
		Assert.That(end.Complete, Is.True);
	}

	[TestCase(TInvestInstrumentKind.Share, 111UL)]
	[TestCase(TInvestInstrumentKind.Etf, 111UL)]
	[TestCase(TInvestInstrumentKind.Bond, 111UL)]
	[TestCase(TInvestInstrumentKind.Future, 111UL)]
	[TestCase(TInvestInstrumentKind.Currency, 111UL)]
	[TestCase(TInvestInstrumentKind.StockOption, 64UL)]
	[TestCase(TInvestInstrumentKind.FuturesOption, 64UL)]
	[TestCase(TInvestInstrumentKind.IndexOption, 64UL)]
	public void ContextPermissionsDoNotTurnOptionDefinitionsIntoMarketSupport(TInvestInstrumentKind kind, ulong expected)
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent(kind: kind));
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new InstrumentContextRequest { Symbol = "SYNTH-TESTCC" });
		Assert.That(((InstrumentContextResponse)Read(client)).Features, Is.EqualTo(expected));
	}

	[TestCase("Exchange", false)]
	[TestCase("Exchange", true)]
	[TestCase("IncludeWeekend", false)]
	[TestCase("IncludeWeekend", true)]
	public void ContextSeparatesRequestedCandleModeFromTheRetainedFilter(string source, bool dealer)
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider(), new() { CandleSource = source, IncludeDealerTrades = dealer });
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new InstrumentContextRequest { Symbol = "SYNTH-TESTCC" });
		var context = (InstrumentContextResponse)Read(client);
		Assert.That(context.SourceSemanticsID, Does.Contain($"candles={source};"));
		Assert.That(context.SourceSemanticsID, Does.Contain($"candle-filter={(dealer ? "all-returned" : "exchange")};"));
		Assert.That(context.SourceSemanticsID, Does.Contain($"trades={(dealer ? "exchange+dealer" : "exchange")};"));
		Assert.That(context.SourceSemanticsID, Does.Contain("weekend=unknown"));
	}

	static TcpClient Connect(TInvestTestHost host)
	{
		var client = new TcpClient { NoDelay = true, ReceiveTimeout = 1500 };
		client.Connect(IPAddress.Loopback, host.Port);
		host.WaitUntilConnected();
		return client;
	}

	static void Send(TcpClient client, ProviderInformationMessage message)
	{
		var bytes = new byte[65535];
		var count = ProviderInformationCodec.EncodeVls(message, bytes);
		client.GetStream().Write(bytes, 0, count);
	}

	static ProviderInformationMessage Read(TcpClient client)
	{
		var stream = client.GetStream();
		var header = new byte[4];
		stream.ReadExactly(header);
		var frame = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header)];
		header.CopyTo(frame, 0);
		stream.ReadExactly(frame, 4, frame.Length - 4);
		return ProviderInformationCodec.DecodeVls(frame);
	}
}
