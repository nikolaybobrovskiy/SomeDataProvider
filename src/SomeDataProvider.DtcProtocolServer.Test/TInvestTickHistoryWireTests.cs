namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Client;
using Dtc.Protocol;
using Dtc.Protocol.Messages;
using Dtc.Protocol.Vls;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Market;

[TestFixture]
[NonParallelizable]
public class TInvestTickHistoryWireTests
{
	static readonly DateTimeOffset Day = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

	[Test]
	public async Task DtcClientReadsTicksWithExactMicrosecondsSidesAndOneFinalRecord()
	{
		using var fixture = new Hosted();
		fixture.Archive(Day, TickFixture.Gzip(TickFixture.Csv(
			TickFixture.Row("2026-10-28T06:59:53.115448Z", "BUY", "310.5", 3),
			TickFixture.Row("2026-10-28T07:00:00Z", "SELL", "310.25", 2),
			TickFixture.Row("2026-10-28T07:00:01Z", "BUY", "300", 9, "DEALER"),
			TickFixture.Row("2026-10-28T07:00:02.000001Z", "BUY", "311", 1))));
		var events = await Download(fixture, Day, Day.AddDays(1));
		var header = events.Single(e => e.Kind == DtcHistoricalDataEventKind.Header).Header!;
		Assert.That(header.RecordInterval, Is.EqualTo(HistoricalDataIntervalEnum.INTERVAL_TICK));
		var ticks = events.Where(e => e.Kind == DtcHistoricalDataEventKind.Tick).Select(e => e.Tick).ToList();
		Assert.That(ticks, Has.Count.EqualTo(3), "The dealer row is dropped.");
		Assert.That(ticks.Select(t => t.AtBidOrAsk), Is.EqualTo(new[] { AtBidOrAskEnum.AT_ASK, AtBidOrAskEnum.AT_BID, AtBidOrAskEnum.AT_ASK }));
		Assert.That(ticks.Select(t => t.Price), Is.EqualTo(new[] { 310.5, 310.25, 311 }));
		Assert.That(ticks.Select(t => t.Volume), Is.EqualTo(new[] { 30d, 20, 10 }), "Volume is quantity x lot (lot 10).");
		Assert.That(ticks.Select(t => t.IsFinalRecord), Is.EqualTo(new[] { false, false, true }));
		var exact = ticks.Select(t => t.Time!.Value.UnixMicroseconds).ToList();
		Assert.That(exact, Is.EqualTo(new[] { Micros(6, 59, 53, 115448), Micros(7, 0, 0, 0), Micros(7, 0, 2, 1) }), "Every microsecond survives the double-seconds round trip.");
		Assert.That(events.Last().Kind, Is.EqualTo(DtcHistoricalDataEventKind.Completed));
	}

	[Test]
	public async Task ADayWithNoTradingAnswersAHeaderWithNoRecords()
	{
		using var fixture = new Hosted();
		var events = await Download(fixture, Day, Day.AddDays(1));
		Assert.That(events.Single(e => e.Kind == DtcHistoricalDataEventKind.Header).Header!.NoRecordsToReturn, Is.True);
		Assert.That(events.Count(e => e.Kind == DtcHistoricalDataEventKind.Tick), Is.EqualTo(0));
	}

	[TestCase(0, 29, "ticks need a start time")]
	[TestCase(1, 29, "UTC days")]
	public void CapsAndMissingStartRejectBeforeTheHeader(int maxDays, int endDay, string text)
	{
		using var fixture = new Hosted(new TInvestOptions { MaxTickDaysPerRequest = maxDays == 0 ? 31 : maxDays });
		using var client = fixture.Connect();
		var start = maxDays == 0 ? DateTimeOffset.UnixEpoch : Day;
		Send(client, start, new DateTimeOffset(2026, 10, endDay, 0, 0, 0, TimeSpan.Zero) + (maxDays == 0 ? TimeSpan.Zero : TimeSpan.FromDays(1)));
		var reject = Reject(Read(client), text);
		Assert.That(reject.RejectReasonCode, Is.EqualTo(HistoricalPriceDataRejectReasonCodeEnum.HPDR_GENERAL_REJECT_ERROR));
		Assert.That(fixture.Gateway.ArchiveRequests, Is.Empty);
	}

	[Test]
	public void TickCapRejectsBeforeTheHeaderWithTheSizeInTheText()
	{
		using var fixture = new Hosted(new TInvestOptions { MaxTicksPerRequest = 1000 });
		fixture.Archive(Day, TickFixture.Gzip(TickFixture.Csv(Enumerable.Range(0, 1001).Select(_ => TickFixture.Row("2026-10-28T07:00:00Z", "BUY", "1", 1)).ToArray())));
		using var client = fixture.Connect();
		Send(client, Day, Day.AddDays(1));
		Reject(Read(client), "1001 ticks");
	}

	[Test]
	public void AColdRangeBeyondTheBudgetAnswersRetryInWithSeconds()
	{
		using var fixture = new Hosted();
		for (var i = 0; i < 27; i++) Assert.That(fixture.Gateway.Limiter.TryAcquire(TInvestLimitGroup.Archives), Is.EqualTo(TimeSpan.Zero));
		using var client = fixture.Connect();
		Send(client, Day, Day.AddDays(1));
		var reject = Reject(Read(client), "retry in 5 s");
		Assert.That(reject.RejectReasonCode, Is.EqualTo(HistoricalPriceDataRejectReasonCodeEnum.HPDR_UNABLE_TO_SERVE_DATA_RETRY_IN_SPECIFIED_SECONDS));
		Assert.That(reject.RetryTimeInSeconds, Is.EqualTo(5));
	}

	[Test]
	public void ASourceFailureAfterTheHeaderClosesTheConnectionWithoutAFinalRecordOrReject()
	{
		using var fixture = new Hosted();
		fixture.Archive(Day, TickFixture.Gzip(TickFixture.Csv(TickFixture.Row("2026-10-28T07:00:00Z", "BUY", "1", 1))));
		fixture.Archive(Day.AddDays(1), TickFixture.Gzip(TickFixture.Csv(TickFixture.Row("2026-10-29T07:00:00Z", "BUY", "2", 1))));
		fixture.Host.Store.BeforeArchiveLoad = path =>
		{
			// The second day's file disappears after the pre-header checks passed.
			if (path.EndsWith("2026-10-29.archive", StringComparison.Ordinal)) File.Delete(path);
		};
		using var client = fixture.Connect();
		Send(client, Day, Day.AddDays(2));
		var header = Read(client);
		Assert.That(DtcWire.TypeOf(header), Is.EqualTo(DtcWire.HistoricalPriceDataResponseHeaderType));
		// The first day is sent before the second one is loaded, so its tick arrives, and it is not the final one.
		var first = Read(client);
		Assert.That(DtcWire.TypeOf(first), Is.Not.EqualTo(DtcWire.HistoricalPriceDataResponseHeaderType));
		Assert.Throws<EndOfStreamException>(() => Read(client), "No final record and no reject follow the failure.");
	}

	[Test]
	public void AFirstDayThatFailsToLoadStillGetsItsHeaderAndThenClosesTheConnection()
	{
		using var fixture = new Hosted();
		fixture.Archive(Day, TickFixture.Gzip(TickFixture.Csv(TickFixture.Row("2026-10-28T07:00:00Z", "BUY", "1", 1))));
		fixture.Host.Store.BeforeArchiveLoad = path =>
		{
			// The only file disappears after the counts were taken, before it is read.
			File.Delete(path);
		};
		using var client = fixture.Connect();
		Send(client, Day, Day.AddDays(1));
		var header = Read(client);
		Assert.That(DtcWire.TypeOf(header), Is.EqualTo(DtcWire.HistoricalPriceDataResponseHeaderType), "The size was known, so the header does not wait for the load.");
		Assert.Throws<EndOfStreamException>(() => Read(client));
	}

	[Test]
	public async Task NotExchangeTradesRecordedByTheRecorderAreNeverReadBack()
	{
		var root = Path.Combine(Path.GetTempPath(), "sdp-rec-" + Guid.NewGuid().ToString("N"));
		var clock = new ManualTimeProvider();
		clock.Advance(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch);
		try
		{
			using var recorder = new TInvestTickRecorder(root, clock);
			ITickRecording recording = recorder;
			recorder.SetLive("uid", true);
			recorder.Record(new TInvestTrade("uid", new(new DateTimeOffset(2026, 10, 30, 11, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), 5), 1, 2, TInvestDirection.Buy, true));
			recorder.Record(new TInvestTrade("uid", new(new DateTimeOffset(2026, 10, 30, 11, 0, 1, TimeSpan.Zero).ToUnixTimeSeconds(), 0), 9, 9, TInvestDirection.Sell, false));
			recorder.Record(new TInvestTrade("uid", new(new DateTimeOffset(2026, 10, 30, 11, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds(), 0), 3, 4, TInvestDirection.Sell, true));
			clock.Advance(TimeSpan.FromSeconds(5));
			recorder.Confirm("uid", clock.GetUtcNow());
			var day = new DateOnly(2026, 10, 30);
			var all = recording.Read("uid", day, new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero));
			Assert.That(all.Select(t => t.Price), Is.EqualTo(new[] { 1d, 3 }), "The dealer trade is not recorded.");
			Assert.That(all[0].Time.Nanos, Is.EqualTo(5));
			Assert.That(recording.Read("uid", day, new DateTimeOffset(2026, 10, 30, 11, 0, 0, TimeSpan.Zero).AddTicks(1), new DateTimeOffset(2026, 10, 30, 11, 30, 0, TimeSpan.Zero)), Is.Empty, "Half-open range, nanosecond precision.");
			Assert.That(recording.Coverage("uid", day).Single().End, Is.EqualTo(clock.GetUtcNow().AddSeconds(-1)));
			Assert.That(recording.Read("other", day, DateTimeOffset.MinValue, DateTimeOffset.MaxValue), Is.Empty);
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}

		using var memory = new TInvestTickRecorder(null, clock);
		memory.SetLive("uid", true);
		clock.Advance(TimeSpan.FromSeconds(3));
		memory.Confirm("uid", clock.GetUtcNow());
		memory.Flush();
		Assert.That(((ITickRecording)memory).Coverage("uid", new DateOnly(2026, 10, 30)), Is.Empty, "An in-memory recorder proves nothing for history.");
		await Task.CompletedTask;
	}

	static long Micros(int hour, int minute, int second, int micro) => new DateTimeOffset(2026, 10, 28, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds() * 1_000_000 + micro;

	static async Task<List<DtcHistoricalDataEvent>> Download(Hosted fixture, DateTimeOffset start, DateTimeOffset end)
	{
		await using var pool = new DtcHistoricalDataPool(fixture.Host.ClientSettings(), new DtcHistoricalDataPoolSettings { ConnectionLimit = 1, RejectRetryLimit = 0 });
		var events = new List<DtcHistoricalDataEvent>();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		await foreach (var e in pool.DownloadAsync(new HistoricalPriceDataRequest
		{
			Symbol = "SYNTH",
			Exchange = "TESTCC",
			RecordInterval = HistoricalDataIntervalEnum.INTERVAL_TICK,
			StartDateTime = DtcTimestamp.FromDateTimeOffset(start),
			EndDateTime = DtcTimestamp.FromDateTimeOffset(end),
		}, timeout.Token))
		{
			events.Add(e);
		}

		return events;
	}

	static void Send(TcpClient client, DateTimeOffset start, DateTimeOffset end)
	{
		var bytes = new byte[256];
		var length = new HistoricalPriceDataRequest { RequestID = 71, Symbol = "SYNTH", Exchange = "TESTCC", RecordInterval = HistoricalDataIntervalEnum.INTERVAL_TICK, StartDateTime = DtcTimestamp.FromDateTimeOffset(start), EndDateTime = DtcTimestamp.FromDateTimeOffset(end) }.ToVls(bytes, Encoding.UTF8);
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

	sealed class Hosted : IDisposable
	{
		readonly string _folder = Path.Combine(Path.GetTempPath(), "sdp-wire-" + Guid.NewGuid().ToString("N"));

		public Hosted(TInvestOptions? options = null)
		{
			Clock.Advance(Day.AddDays(4) - DateTimeOffset.UnixEpoch);
			Gateway = new ScriptedTInvestGateway { Limiter = new(Clock) };
			Gateway.Instruments.Add(ScriptedTInvestGateway.Invent() with { FirstMinuteCandle = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) });
			var folder = _folder;
			Host = new TInvestTestHost(Gateway, Clock, options == null ? new TInvestOptions { ArchiveCacheFolder = folder } : new TInvestOptions
			{
				ArchiveCacheFolder = folder,
				MaxTickDaysPerRequest = options.MaxTickDaysPerRequest,
				MaxTicksPerRequest = options.MaxTicksPerRequest,
			});
		}

		public ManualTimeProvider Clock { get; } = new();
		public ScriptedTInvestGateway Gateway { get; }
		public TInvestTestHost Host { get; }

		public void Archive(DateTimeOffset day, byte[] bytes) => Gateway.ArchiveFiles[("SYNTH_TESTCC", DateOnly.FromDateTime(day.UtcDateTime))] = bytes;

		public TcpClient Connect()
		{
			var client = new TcpClient { ReceiveTimeout = 10000 };
			client.Connect(IPAddress.Loopback, Host.Port);
			Host.WaitUntilConnected();
			return client;
		}

		public void Dispose()
		{
			Host.Dispose();
			if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
		}
	}
}
