namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Protocol;
using Dtc.Protocol.Messages;
using Dtc.Protocol.Vls;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;

[TestFixture]
[NonParallelizable]
public class TInvestHistoryWireTests
{
	static readonly DateTimeOffset Day = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

	[TestCase("SYNTH", "TESTCC", false)]
	[TestCase("SYNTH-TESTCC", "", false)]
	[TestCase("SYNTH", "TESTCC", true)]
	public void BarsHaveOneHeaderCorrectIntervalHalfOpenBoundsAndOneFinal(string symbol, string exchange, bool empty)
	{
		using var gateway = Gateway();
		gateway.Candles = (request, _) => Task.FromResult<IReadOnlyList<TInvestCandle>>(empty ? Array.Empty<TInvestCandle>() : new[]
		{
			Candle(request.End), Candle(request.Start.AddMinutes(1)), Candle(request.Start),
		});
		using var host = new TInvestTestHost(gateway);
		using var client = Connect(host);
		Send(client, symbol, exchange, 60, Day, Day.AddDays(1));
		var frames = new List<byte[]> { Read(client) };
		if (!empty)
		{
			frames.Add(Read(client));
			frames.Add(Read(client));
		}
		var response = DtcWire.DecodeHistoryResponse(frames);
		Assert.That(response.Header.RecordInterval, Is.EqualTo(60));
		Assert.That(response.Records.Select(r => r.StartDateTime), Is.EqualTo(empty ? Array.Empty<long>() : new[] { Day.ToUnixTimeSeconds() * 1_000_000, Day.AddMinutes(1).ToUnixTimeSeconds() * 1_000_000 }));
		Assert.That(gateway.Lookups.Single(), Is.EqualTo(("SYNTH", "TESTCC")));
	}

	[TestCase(0, "UTC days")]
	[TestCase(1, "Interval not supported")]
	[TestCase(5, "last month")]
	public void UnsupportedTickIntervalAndOldSecondRangesRejectBeforeHeader(int interval, string text)
	{
		using var gateway = Gateway();
		using var host = new TInvestTestHost(gateway);
		using var client = Connect(host);
		Send(client, "SYNTH", "TESTCC", interval, Day.AddYears(-1), Day);
		var reject = Reject(Read(client), text);
		Assert.That(reject.RejectReasonCode, Is.EqualTo(HistoricalPriceDataRejectReasonCodeEnum.HPDR_GENERAL_REJECT_ERROR));
		Assert.That(gateway.CandleRequests, Is.Empty);
	}

	[Test]
	public void CallsCapRejectsBeforeHeader()
	{
		using var gateway = Gateway();
		using var host = new TInvestTestHost(gateway, options: new() { MaxCandleCallsPerRequest = 1 });
		using var client = Connect(host);
		Send(client, "SYNTH", "TESTCC", 60, Day, Day.AddDays(2));
		Reject(Read(client), "request a shorter range");
		Assert.That(gateway.CandleRequests, Is.Empty);
	}

	[TestCase(5, false)]
	[TestCase(10, false)]
	[TestCase(30, false)]
	[TestCase(5, true)]
	[TestCase(10, true)]
	[TestCase(30, true)]
	public void ZeroSecondStartSelectsRetentionBoundaryOverOldOrMissingMetadata(int interval, bool missingMetadata)
	{
		var clock = Clock();
		using var gateway = Gateway(clock);
		gateway.Instruments[0] = gateway.Instruments[0] with { FirstMinuteCandle = missingMetadata ? null : clock.GetUtcNow().AddYears(-5) };
		gateway.Candles = (request, _) => Task.FromResult<IReadOnlyList<TInvestCandle>>(new[] { Candle(request.Start) });
		using var host = new TInvestTestHost(gateway, clock);
		using var client = Connect(host);
		var retained = clock.GetUtcNow().AddDays(-30);
		Send(client, "SYNTH", "TESTCC", interval, DateTimeOffset.UnixEpoch, retained.AddMinutes(1));
		var response = DtcWire.DecodeHistoryResponse(new[] { Read(client), Read(client) });
		Assert.That(response.Records.Single().StartDateTime, Is.EqualTo(retained.ToUnixTimeSeconds() * 1_000_000));
		Assert.That(gateway.CandleRequests.Single().Start, Is.EqualTo(retained));
	}

	[Test]
	public void UnexpectedSourceFailureBeforeHeaderGetsSafeGeneralReject()
	{
		using var gateway = Gateway();
		gateway.Candles = (_, _) => throw new InvalidOperationException("Unsafe upstream exception payload.");
		using var host = new TInvestTestHost(gateway);
		using var client = Connect(host);
		Send(client, "SYNTH", "TESTCC", 60, Day, Day.AddDays(1));
		var frame = Read(client);
		var reject = Reject(frame, "T-Invest source request failed.");
		Assert.That(reject.RejectReasonCode, Is.EqualTo(HistoricalPriceDataRejectReasonCodeEnum.HPDR_GENERAL_REJECT_ERROR));
		Assert.That(DtcVlsDecoder.GetString(frame, reject.RejectText, Encoding.UTF8), Does.Not.Contain("Unsafe upstream"));
	}

	[Test]
	public void SharedQuotaRejectHasRetryInBeforeHeader()
	{
		var clock = Clock();
		using var gateway = Gateway(clock);
		for (var i = 0; i < 539; i++)
		{
			if (i > 0 && i % 45 == 0) clock.Advance(TimeSpan.FromSeconds(1));
			Assert.That(gateway.Limiter.TryAcquire(TInvestLimitGroup.MarketData), Is.EqualTo(TimeSpan.Zero));
		}
		using var host = new TInvestTestHost(gateway, clock);
		using var client = Connect(host);
		Send(client, "SYNTH", "TESTCC", 60, Day.AddDays(-365), Day);
		var reject = Reject(Read(client), "retry in");
		Assert.That(reject.RejectReasonCode, Is.EqualTo(HistoricalPriceDataRejectReasonCodeEnum.HPDR_UNABLE_TO_SERVE_DATA_RETRY_IN_SPECIFIED_SECONDS));
		Assert.That(reject.RetryTimeInSeconds, Is.InRange(1, 300));
		Assert.That(gateway.CandleRequests, Is.Empty);
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task SourceFailureOrRecordTimeoutAfterHeaderClosesWithoutFinalOrReject(bool timeout)
	{
		var clock = Clock();
		using var gateway = Gateway(clock);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		gateway.Candles = (request, ct) =>
		{
			if (request.Start == Day) return Task.FromResult<IReadOnlyList<TInvestCandle>>(new[] { Candle(Day) });
			entered.SetResult();
			return timeout ? new TaskCompletionSource<IReadOnlyList<TInvestCandle>>().Task.WaitAsync(ct)
				: Task.FromException<IReadOnlyList<TInvestCandle>>(new TInvestRequestException("Scripted source failure."));
		};
		using var host = new TInvestTestHost(gateway, clock);
		using var client = Connect(host);
		Send(client, "SYNTH", "TESTCC", 60, Day, Day.AddDays(2));
		Assert.That(DtcWire.TypeOf(Read(client)), Is.EqualTo(801));
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		if (timeout) clock.Advance(TimeSpan.FromSeconds(20));
		Assert.That(client.GetStream().ReadByte(), Is.EqualTo(-1), "No final record and no post-header reject.");
	}

	[Test]
	public async Task EmptyPagesDoNotResetPreHeaderBudget()
	{
		var clock = Clock();
		using var gateway = Gateway(clock);
		gateway.Candles = (request, _) =>
		{
			clock.Advance(TimeSpan.FromSeconds(11));
			return Task.FromResult<IReadOnlyList<TInvestCandle>>(Array.Empty<TInvestCandle>());
		};
		using var host = new TInvestTestHost(gateway, clock);
		using var client = Connect(host);
		Send(client, "SYNTH", "TESTCC", 60, Day, Day.AddDays(2));
		var reject = Reject(await Task.Run(() => Read(client)), "deadline");
		Assert.That(reject.RejectReasonCode, Is.EqualTo(HistoricalPriceDataRejectReasonCodeEnum.HPDR_UNABLE_TO_SERVE_DATA_RETRY_IN_SPECIFIED_SECONDS));
	}

	static TInvestCandle Candle(DateTimeOffset time) => new(time, 11, 14, 10, 12, 7, true, true);

	static ScriptedTInvestGateway Gateway(TimeProvider? clock = null)
	{
		var gateway = new ScriptedTInvestGateway { Limiter = new(clock) };
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		return gateway;
	}

	static ManualTimeProvider Clock()
	{
		var clock = new ManualTimeProvider();
		clock.Advance(Day.AddDays(4) - DateTimeOffset.UnixEpoch);
		return clock;
	}

	static TcpClient Connect(TInvestTestHost host)
	{
		var client = new TcpClient { ReceiveTimeout = 10000 };
		client.Connect(IPAddress.Loopback, host.Port);
		host.WaitUntilConnected();
		return client;
	}

	static void Send(TcpClient client, string symbol, string exchange, int interval, DateTimeOffset start, DateTimeOffset end)
	{
		var bytes = new byte[256];
		var length = new HistoricalPriceDataRequest { RequestID = 71, Symbol = symbol, Exchange = exchange, RecordInterval = (HistoricalDataIntervalEnum)interval, StartDateTime = DtcTimestamp.FromDateTimeOffset(start), EndDateTime = DtcTimestamp.FromDateTimeOffset(end) }.ToVls(bytes, Encoding.UTF8);
		client.GetStream().Write(bytes.AsSpan(0, length));
	}

	static byte[] Read(TcpClient client)
	{
		var header = new byte[4];
		client.GetStream().ReadExactly(header);
		var frame = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header)];
		header.CopyTo(frame, 0);
		client.GetStream().ReadExactly(frame.AsSpan(4));
		return frame;
	}

	static Dtc.Protocol.Vls.s_HistoricalPriceDataReject Reject(byte[] frame, string text)
	{
		Assert.That(DtcWire.TypeOf(frame), Is.EqualTo(802));
		DtcVlsDecoder.Decode(frame, out Dtc.Protocol.Vls.s_HistoricalPriceDataReject reject);
		Assert.That(DtcVlsDecoder.GetString(frame, reject.RejectText, Encoding.UTF8), Does.Contain(text));
		Assert.That(reject.RequestID, Is.EqualTo(71));
		return reject;
	}
}
