namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Buffers.Binary;
using Dtc.Protocol;
using Dtc.Protocol.Messages;
using Dtc.Protocol.Private;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest.Test;

public partial class ProviderInformationWireTests
{
	[Test]
	public void QuietRecentIntervalWithoutRecorderHasExactProofBeforeEmptyHeader()
	{
		var clock = new ManualTimeProvider();
		clock.Advance(TimeSpan.FromDays(20000));
		using var gateway = new ScriptedTInvestGateway { Limiter = new(clock) };
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var host = new TInvestTestHost(gateway, clock, onlyHistoryServer: true);
		using var main = Connect(host);
		Send(main, new ProviderHelloRequest { BudgetScopeID = "synthetic-profile" });
		Read(main);
		Send(main, new InstrumentContextRequest { Symbol = "SYNTH-TESTCC" });
		var context = (InstrumentContextResponse)Read(main);
		using var history = Connect(host);
		Send(history, new ProviderHelloRequest { Mode = ProviderHelloModes.JoinHistorical, BudgetScopeID = "synthetic-profile", Source = context.Source, InstrumentUID = context.InstrumentUID, UIDEpoch = context.UIDEpoch });
		Assert.That(Read(history), Is.TypeOf<ProviderHelloResponse>());
		var request = new RecentTickReconcileRequest
		{
			RequestID = 70, Symbol = context.Symbol, Source = context.Source, InstrumentUID = context.InstrumentUID!, UIDEpoch = context.UIDEpoch,
			SourceSemanticsID = context.SourceSemanticsID, BudgetScopeID = "synthetic-profile", From = clock.GetUtcNow().AddMinutes(-5).ToUnixTimeSeconds(), To = clock.GetUtcNow().AddSeconds(-2).ToUnixTimeSeconds(), MaximumTicks = 1000,
		};
		Send(history, request);
		var answer = Read(history);
		Assert.That(answer, Is.TypeOf<SourceOperationStatus>(), (answer as ProviderRequestReject)?.ReasonCode);
		var status = (SourceOperationStatus)answer;
		Assert.That(status.Status, Is.EqualTo(1));
		Assert.That(status.AcceptedFrom, Is.EqualTo(request.From));
		Assert.That(status.AcceptedTo, Is.EqualTo(request.To));
		Assert.That(status.UIDEpoch, Is.EqualTo(context.UIDEpoch));
		Assert.That(status.SourceSemanticsID, Is.EqualTo(context.SourceSemanticsID));
		Assert.That(status.ProviderUtcMicroseconds, Is.EqualTo((clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks / 10));
		var stream = history.GetStream();
		var head = new byte[4];
		stream.ReadExactly(head);
		var frame = new byte[BinaryPrimitives.ReadUInt16LittleEndian(head)];
		head.CopyTo(frame, 0);
		stream.ReadExactly(frame, 4, frame.Length - 4);
		DtcBinaryDecoder.Decode<s_HistoricalPriceDataResponseHeader>(frame, out var header);
		Assert.That(header.RequestID, Is.EqualTo(70));
		Assert.That(header.NoRecordsToReturn, Is.EqualTo(1));
		Send(history, new ProviderHelloRequest { Mode = ProviderHelloModes.ClockOnly, BudgetScopeID = request.BudgetScopeID, Source = request.Source, InstrumentUID = request.InstrumentUID, UIDEpoch = request.UIDEpoch });
		Assert.That(Read(history), Is.TypeOf<ProviderHelloResponse>(), "No trailing final tick follows the empty header.");
		Assert.That(gateway.LastTradeRequests, Is.EqualTo(new[] { (DateTimeOffset.FromUnixTimeSeconds(request.From), DateTimeOffset.FromUnixTimeSeconds(request.To)) }));
		Assert.That(gateway.Lookups, Has.Count.EqualTo(1));
		Assert.That(gateway.ArchiveRequests, Is.Empty);
		Assert.That(gateway.CandleRequests, Is.Empty);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void RecentLaneAndClockRefreshAreAdvertisedTogether(bool dealer)
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider(), new() { IncludeDealerTrades = dealer });
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-profile" });
		var hello = (ProviderHelloResponse)Read(client);
		Assert.That(hello.Features & ProviderInformationFeatures.RecentClosedTicksV1, Is.EqualTo(dealer ? 0UL : ProviderInformationFeatures.RecentClosedTicksV1));
		Assert.That(hello.Features & ProviderInformationFeatures.ClockRefreshV1, Is.EqualTo(dealer ? 0UL : ProviderInformationFeatures.ClockRefreshV1));
		Assert.That(gateway.Lookups, Is.Empty);
	}
}
