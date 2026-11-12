namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
using Dtc.Protocol.Private;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;

public partial class ProviderInformationWireTests
{
	[Test]
	public void SourceStartingWithoutSourceRetryUsesThirtySeconds()
	{
		using var gateway = new ScriptedTInvestGateway { Resolve = (_, _, _) => throw new TInvestRequestException("Synthetic startup", TInvestFailureKind.Starting) };
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new InstrumentContextRequest { Symbol = "SYNTH-TESTCC" });
		var reject = (ProviderRequestReject)Read(client);
		Assert.That(reject.ReasonCode, Is.EqualTo("SourceStarting"));
		Assert.That(reject.RetryAfterSeconds, Is.EqualTo(30));
	}

	[TestCase(TInvestFailureKind.Source, "SourceUnavailable")]
	[TestCase(TInvestFailureKind.Starting, "SourceStarting")]
	[TestCase(TInvestFailureKind.Configuration, "SourceNotConfigured")]
	[TestCase(TInvestFailureKind.Limit, "LimitRejected")]
	[TestCase(TInvestFailureKind.Unsupported, "FeatureUnavailable")]
	public void SourceRejectsHaveStableCodesAndTypedRetry(TInvestFailureKind kind, string reason)
	{
		using var gateway = new ScriptedTInvestGateway { Resolve = (_, _, _) => throw new TInvestRequestException("Do not echo source details.", kind, 17) };
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new InstrumentContextRequest { RequestID = 61, Symbol = "SYNTH-TESTCC" });
		var reject = (ProviderRequestReject)Read(client);
		Assert.That(reject.RequestID, Is.EqualTo(61));
		Assert.That(reject.FailedOperation, Is.EqualTo(20102));
		Assert.That(reject.ReasonCode, Is.EqualTo(reason));
		Assert.That(reject.RetryAfterSeconds, Is.EqualTo(17));
		Assert.That(reject.Text, Does.Not.Contain("Do not echo"));
	}

	[Test]
	public void UnknownOptionalContextFieldsAreAbsentNotZero()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(new(new("SYNTH", "TESTCC", string.Empty, string.Empty, TInvestInstrumentKind.Unsupported, 0, false), 0, string.Empty));
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new InstrumentContextRequest { Symbol = "SYNTH-TESTCC" });
		var context = (InstrumentContextResponse)Read(client);
		var optionalNames = new[] { "InstrumentUID", "Board", "BoardName", "Lot", "Currency", "ExactIncrement", "PriceUnit", "MonetaryTickValue", "FirstMinuteDate", "FirstDailyDate", "CalendarID", "CalendarRevision", "MaxTickDaysPerRequest", "MaxTicksPerRequest", "Description" };
		foreach (var name in optionalNames) Assert.That(typeof(InstrumentContextResponse).GetProperty(name)!.GetValue(context), Is.Null, name);
		Assert.That(context.Features, Is.Zero);
		Assert.That(context.SourceSemanticsID, Does.Contain("weekend=unknown"));
	}

	[TestCase("CalendarUnavailable")]
	[TestCase("CalendarRevisionMismatch")]
	[TestCase("CalendarExpired")]
	[TestCase("CalendarIncomplete")]
	[TestCase("SourceMismatch")]
	public void CalendarRejectsAreCorrelatedAndTerminal(string reason)
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		if (reason != "CalendarUnavailable")
		{
			gateway.Calendar = new("synthetic-calendar", "synthetic-r1", "invented-uid-SYNTH", "Synthetic dated rule", CalendarTests.Friday, CalendarTests.Monday,
				reason == "CalendarExpired" ? clock.GetUtcNow() : CalendarTests.Expires, reason == "SourceMismatch", "Exchange", Array.Empty<TInvestCalendarWindow>());
		}
		using var host = new TInvestTestHost(gateway, clock);
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new TradingCalendarRequest { RequestID = 62, CalendarID = "synthetic-calendar", CalendarRevision = reason == "CalendarRevisionMismatch" ? "synthetic-r2" : "synthetic-r1", FromTradeDate = 20261106, ToTradeDate = reason == "CalendarIncomplete" ? 20261110 : 20261109 });
		var reject = (ProviderRequestReject)Read(client);
		Assert.That(reject.ReasonCode, Is.EqualTo(reason));
		Assert.That(reject.RequestID, Is.EqualTo(62));
		Assert.That(reject.FailedOperation, Is.EqualTo(20104));
		Assert.That(reject.RetryAfterSeconds, Is.Null);
		// A subsequent operation must not encounter a stray END from the rejected calendar.
		Send(client, new InstrumentContextRequest { RequestID = 63, Symbol = "SYNTH-TESTCC" });
		Assert.That(Read(client).RequestID, Is.EqualTo(63));
	}

	[TestCase("SYNTH", "InvalidRequest")]
	[TestCase("SYNTH-TESTCC", "UnknownSymbol")]
	[TestCase("fred-SYNTH", "FeatureUnavailable")]
	public void ContextIdentityRejectsDoNotSearchOtherSources(string symbol, string reason)
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new InstrumentContextRequest { Symbol = symbol });
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo(reason));
		Assert.That(gateway.Searches, Is.Zero);
	}

	[Test]
	public void MalformedCalendarIsRejectedWithoutClosingConnection()
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		var bytes = new byte[65535];
		var count = ProviderInformationCodec.EncodeVls(new TradingCalendarRequest { RequestID = 65, CalendarID = "synthetic-calendar", CalendarRevision = "synthetic-r1", FromTradeDate = 20261101, ToTradeDate = 20261130 }, bytes);
		BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), 20261203);
		client.GetStream().Write(bytes, 0, count);
		var reject = (ProviderRequestReject)Read(client);
		Assert.That(reject.RequestID, Is.EqualTo(65));
		Assert.That(reject.ReasonCode, Is.EqualTo("InvalidRequest"));
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Assert.That(Read(client), Is.TypeOf<ProviderHelloResponse>());
	}

	[TestCase(ProviderHelloModes.JoinHistorical)]
	[TestCase(ProviderHelloModes.ClockOnly)]
	public void UnsupportedHelloModesDoNotConsumeNegotiation(int mode)
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { RequestID = 66, Mode = mode, BudgetScopeID = "synthetic-generation", Source = "t-invest", InstrumentUID = "synthetic-uid", UIDEpoch = "synthetic-epoch" });
		var reject = (ProviderRequestReject)Read(client);
		Assert.That(reject.RequestID, Is.EqualTo(66));
		Assert.That(reject.FailedOperation, Is.EqualTo(20100));
		Assert.That(reject.ReasonCode, Is.EqualTo("Unsupported"));
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Assert.That(Read(client), Is.TypeOf<ProviderHelloResponse>());
		Assert.That(gateway.Lookups, Is.Empty);
	}

	[Test]
	public void ProbeHelloIsRejectedWithoutAdmittingSourceWork()
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation", ProbeScopeID = "synthetic-probe", ProbeTInvestAttempts = 1, ProbeFredAttempts = 0 });
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("Unsupported"));
		Assert.That(gateway.Lookups, Is.Empty);
		Assert.That(gateway.Searches, Is.Zero);
	}

	[Test]
	public void UnexpectedSourceErrorsAreNotEchoedAndDoNotCloseTheConnection()
	{
		using var gateway = new ScriptedTInvestGateway { Resolve = (_, _, _) => throw new InvalidOperationException("Synthetic unsafe source details") };
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		Send(client, new InstrumentContextRequest { RequestID = 67, Symbol = "SYNTH-TESTCC" });
		var reject = (ProviderRequestReject)Read(client);
		Assert.That(reject.ReasonCode, Is.EqualTo("SourceUnavailable"));
		Assert.That(reject.Text, Does.Not.Contain("Synthetic unsafe"));
		Assert.That(reject.RetryAfterSeconds, Is.Null);
		gateway.Resolve = null;
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		Send(client, new InstrumentContextRequest { RequestID = 68, Symbol = "SYNTH-TESTCC" });
		Assert.That(Read(client).RequestID, Is.EqualTo(68));
	}

	[Test]
	public void RequestsBeforeHelloAndRepeatedNegotiationCannotEnableFeatures()
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new InstrumentContextRequest { Symbol = "SYNTH-TESTCC" });
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("FeatureUnavailable"));
		Send(client, new ProviderHelloRequest { MinimumVersion = 2, MaximumVersion = 3, BudgetScopeID = "synthetic-generation" });
		var hello = (ProviderHelloResponse)Read(client);
		Assert.That(hello.SelectedVersion, Is.Zero);
		Assert.That(hello.Features, Is.Zero);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("AlreadyNegotiated"));
		Assert.That(gateway.Lookups, Is.Empty);
	}
}
