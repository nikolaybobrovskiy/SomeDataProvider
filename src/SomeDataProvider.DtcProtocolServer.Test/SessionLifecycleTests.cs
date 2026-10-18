// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Buffers.Binary;
	using System.Linq;
	using System.Net;
	using System.Runtime.CompilerServices;
	using System.Threading;
	using System.Threading.Tasks;

	using Dtc.Protocol;
	using Dtc.Protocol.Vls;

	using Microsoft.Extensions.Logging;

	using NUnit.Framework;

	using SomeDataProvider.DataStorage.Definitions;

	using s_LogonRequest = Dtc.Protocol.Vls.s_LogonRequest;
	using s_LogonResponse = Dtc.Protocol.Vls.s_LogonResponse;

	public partial class SessionTests
	{
		[Test]
		public void StreamingLogon_WithZeroInterval_Succeeds()
		{
			var clock = new ManualTimeProvider();
			using var connection = Connect(new FakeHistory(0), onlyHistoryServer: false, timeProvider: clock);
			Logon(connection, 0);
			Assert.That(clock.ActiveTimers, Is.Zero);
			clock.Advance(TimeSpan.FromMinutes(5));
			AssertResponsive(connection);
			connection.AssertNothingMoreArrives();
		}

		[Test]
		public void SilentClient_IsClosedAtTwiceItsInterval_NotBefore()
		{
			var clock = new ManualTimeProvider();
			var logs = new RecordingLoggerFactory();
			using var connection = Connect(new FakeHistory(0), logs, false, clock);
			Logon(connection, 10);
			clock.Advance(TimeSpan.FromSeconds(19));
			Assert.That(connection.IsSessionConnected, Is.True);
			AssertHeartbeat(connection);
			clock.Advance(TimeSpan.FromSeconds(1));
			connection.AssertClosedByServer();
			clock.Advance(TimeSpan.FromMinutes(5));
			var closes = logs.WithTemplate("Client messages timed out; closing connection: {remoteEndPoint}");
			Assert.That(closes, Has.Count.EqualTo(1));
			Assert.That(closes[0].Level, Is.EqualTo(LogLevel.Information));
			Assert.That(closes[0].Values["remoteEndPoint"], Is.TypeOf<IPEndPoint>());
			Assert.That(clock.ActiveTimers, Is.Zero);
		}

		[TestCase(true)]
		[TestCase(false)]
		public void CompleteClientMessages_KeepTheSessionAliveForMinutes(bool heartbeatsOnly)
		{
			var clock = new ManualTimeProvider();
			var logs = new RecordingLoggerFactory();
			using var connection = Connect(new FakeHistory(0), logs, false, clock);
			Logon(connection, 10);
			for (var i = 1; i <= 30; i++)
			{
				clock.Advance(TimeSpan.FromSeconds(10));
				AssertHeartbeat(connection);
				if (heartbeatsOnly)
				{
					connection.Send(Frame(DtcConstants.HEARTBEAT));
					Assert.That(SpinWait.SpinUntil(
						() => logs.WithTemplate("RequestReceived: {requestType}").Count(e => Equals(e.Values["requestType"], "Heartbeat")) == i,
						ResponseTimeout), Is.True, "The client heartbeat was not processed.");
				}
				else
				{
					AssertResponsive(connection);
				}
				Assert.That(connection.IsSessionConnected, Is.True);
			}
			Assert.That(logs.WithTemplate("Client messages timed out; closing connection: {remoteEndPoint}"), Is.Empty);
		}

		[Test]
		public void ActivityBetweenTimerTicks_RestartsTheWholeLossInterval()
		{
			var clock = new ManualTimeProvider();
			using var connection = Connect(new FakeHistory(0), onlyHistoryServer: false, timeProvider: clock);
			Logon(connection, 10);
			clock.Advance(TimeSpan.FromSeconds(19));
			AssertHeartbeat(connection);
			AssertResponsive(connection);
			clock.Advance(TimeSpan.FromSeconds(19));
			Assert.That(connection.IsSessionConnected, Is.True);
			AssertHeartbeat(connection);
			AssertHeartbeat(connection);
			clock.Advance(TimeSpan.FromSeconds(1));
			connection.AssertClosedByServer();
		}

		[Test]
		public void PartialFrame_DoesNotResetTheLossInterval()
		{
			var clock = new ManualTimeProvider();
			var logs = new RecordingLoggerFactory();
			using var connection = Connect(new FakeHistory(0), logs, false, clock);
			Logon(connection, 10);
			clock.Advance(TimeSpan.FromSeconds(19));
			AssertHeartbeat(connection);
			var received = logs.WithTemplate("BytesReceived: {bytesReceived}/{offset}").Count;
			connection.Send(Frame(DtcConstants.HEARTBEAT).AsSpan(0, 3).ToArray());
			Assert.That(SpinWait.SpinUntil(() => logs.WithTemplate("BytesReceived: {bytesReceived}/{offset}").Count > received, ResponseTimeout), Is.True);
			clock.Advance(TimeSpan.FromSeconds(1));
			connection.AssertClosedByServer();
		}

		[TestCase(0)]
		[TestCase(10)]
		public void HistoryOnlyLogon_NeverStartsTimers(int interval)
		{
			var clock = new ManualTimeProvider();
			using var connection = Connect(new FakeHistory(0), timeProvider: clock);
			Logon(connection, interval);
			Assert.That(clock.ActiveTimers, Is.Zero);
			clock.Advance(TimeSpan.FromMinutes(5));
			AssertResponsive(connection);
			connection.AssertNothingMoreArrives();
		}

		[TestCase(-1, 0)]
		[TestCase(301, 300)]
		[TestCase(1, 1)]
		[TestCase(300, 300)]
		public void LogonInterval_IsClampedToZeroThroughThreeHundred(int requested, int accepted)
		{
			var clock = new ManualTimeProvider();
			using var connection = Connect(new FakeHistory(0), onlyHistoryServer: false, timeProvider: clock);
			Logon(connection, requested);
			if (accepted == 0)
			{
				Assert.That(clock.ActiveTimers, Is.Zero);
				clock.Advance(TimeSpan.FromMinutes(5));
				AssertResponsive(connection);
				return;
			}
			clock.Advance(TimeSpan.FromSeconds(accepted));
			AssertHeartbeat(connection);
			Assert.That(connection.IsSessionConnected, Is.True);
			clock.Advance(TimeSpan.FromSeconds(accepted - 1));
			Assert.That(connection.IsSessionConnected, Is.True);
			clock.Advance(TimeSpan.FromSeconds(1));
			connection.AssertClosedByServer();
		}

		[TestCase(false)]
		[TestCase(true)]
		public void Logoff_ClosesConnection_CancelsHistory_AndStopsTimers(bool onlyHistoryServer)
		{
			var clock = new ManualTimeProvider();
			var logs = new RecordingLoggerFactory();
			using var reader = new WaitingHistoryReader();
			var stores = new FakeStores(new FakeHistory(0)) { HistoryReaderFactory = _ => reader };
			using var connection = Connect(stores, logs, onlyHistoryServer, clock);
			Logon(connection, 10);
			connection.Send(DtcWire.EncodeHistoricalPriceDataRequest(42, "fred-GDP", 0, 0));
			Assert.That(reader.Started.Wait(ResponseTimeout), Is.True, "The history request never started.");
			// Frames following LOGOFF in the same read must neither respond nor restart timers.
			connection.Send(Frame(DtcConstants.LOGOFF).Concat(EncodeLogon(10)).Concat(DtcWire.EncodeSecurityDefinitionForSymbolRequest(43, "fred-GDP")).ToArray());
			connection.AssertClosedByServer();
			Assert.That(reader.Cancelled.Wait(ResponseTimeout), Is.True, "The history request was not cancelled.");
			Assert.That(reader.ReaderDisposed.Wait(ResponseTimeout), Is.True, "The history reader was not disposed.");
			clock.Advance(TimeSpan.FromMinutes(5));
			Assert.That(clock.ActiveTimers, Is.Zero);
			var closes = logs.WithTemplate("Client logged off; closing connection: {remoteEndPoint}");
			Assert.That(closes, Has.Count.EqualTo(1));
			Assert.That(closes[0].Level, Is.EqualTo(LogLevel.Information));
			Assert.That(logs.Entries.Where(e => e.Level >= LogLevel.Warning && e.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase)), Is.Empty);
		}

		[Test]
		public void LostClient_CancelsRunningHistory()
		{
			var clock = new ManualTimeProvider();
			using var reader = new WaitingHistoryReader();
			var stores = new FakeStores(new FakeHistory(0)) { HistoryReaderFactory = _ => reader };
			using var connection = Connect(stores, onlyHistoryServer: false, timeProvider: clock);
			Logon(connection, 10);
			connection.Send(DtcWire.EncodeHistoricalPriceDataRequest(42, "fred-GDP", 0, 0));
			Assert.That(reader.Started.Wait(ResponseTimeout), Is.True);
			clock.Advance(TimeSpan.FromSeconds(10));
			AssertHeartbeat(connection);
			clock.Advance(TimeSpan.FromSeconds(10));
			connection.AssertClosedByServer();
			Assert.That(reader.Cancelled.Wait(ResponseTimeout), Is.True);
			Assert.That(reader.ReaderDisposed.Wait(ResponseTimeout), Is.True);
		}

		[Test]
		public void LogonWithZeroInterval_StopsExistingTimers()
		{
			var clock = new ManualTimeProvider();
			using var connection = Connect(new FakeHistory(0), onlyHistoryServer: false, timeProvider: clock);
			Logon(connection, 10);
			Assert.That(clock.ActiveTimers, Is.EqualTo(2));
			Logon(connection, 0);
			Assert.That(clock.ActiveTimers, Is.Zero);
			clock.Advance(TimeSpan.FromMinutes(5));
			AssertResponsive(connection);
		}

		[Test]
		public void PeerDisconnect_StopsExistingTimers()
		{
			var clock = new ManualTimeProvider();
			using var connection = Connect(new FakeHistory(0), onlyHistoryServer: false, timeProvider: clock);
			Logon(connection, 10);
			connection.CloseClient();
			Assert.That(SpinWait.SpinUntil(() => clock.ActiveTimers == 0, ResponseTimeout), Is.True);
			clock.Advance(TimeSpan.FromMinutes(5));
		}

		static void Logon(Connection connection, int interval)
		{
			connection.Send(EncodeLogon(interval));
			var frame = connection.ReadFrame();
			Assert.That(DtcWire.TypeOf(frame), Is.EqualTo(DtcConstants.LOGON_RESPONSE));
			DtcVlsDecoder.Decode(frame, out s_LogonResponse response);
			Assert.That(response.Result, Is.EqualTo(LogonStatusEnum.LOGON_SUCCESS));
			// The round trip also waits for the logon's synchronous timer setup to finish.
			AssertResponsive(connection);
		}

		static void AssertResponsive(Connection connection)
		{
			connection.Send(DtcWire.EncodeSecurityDefinitionForSymbolRequest(99, "fred-GDP"));
			Assert.That(DtcWire.DecodeSecurityDefinitionResponse(connection.ReadFrame()).RequestId, Is.EqualTo(99));
		}

		static void AssertHeartbeat(Connection connection) => Assert.That(DtcWire.TypeOf(connection.ReadFrame()), Is.EqualTo(DtcConstants.HEARTBEAT));

		static byte[] Frame(ushort type)
		{
			var bytes = new byte[type == DtcConstants.HEARTBEAT ? Unsafe.SizeOf<s_Heartbeat>() : Unsafe.SizeOf<Dtc.Protocol.Vls.s_Logoff>()];
			BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)bytes.Length);
			BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), type);
			if (type == DtcConstants.LOGOFF)
				BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)bytes.Length);
			return bytes;
		}

		static byte[] EncodeLogon(int interval)
		{
			var message = new s_LogonRequest { ProtocolVersion = 8, HeartbeatIntervalInSeconds = interval };
			var buffer = new byte[Unsafe.SizeOf<s_LogonRequest>()];
			var encoder = new DtcVlsEncoder<s_LogonRequest>(ref message, buffer);
			return buffer.AsSpan(0, encoder.Complete()).ToArray();
		}

		sealed class WaitingHistoryReader : ISymbolHistoryStoreReader
		{
			public ManualResetEventSlim Started { get; } = new();

			public ManualResetEventSlim Cancelled { get; } = new();

			public ManualResetEventSlim ReaderDisposed { get; } = new();

			public async Task<SymbolHistoryResponse> ReadSymbolHistoryAsync(CancellationToken cancellationToken = default)
			{
				Started.Set();
				try
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
					throw new InvalidOperationException("The infinite wait completed without cancellation.");
				}
				catch (OperationCanceledException)
				{
					Cancelled.Set();
					throw;
				}
			}

			public void Dispose() => ReaderDisposed.Set();
		}
	}
}
