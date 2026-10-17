// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Buffers.Binary;
	using System.Collections.Generic;
	using System.Linq;
	using System.Net;
	using System.Net.Sockets;
	using System.Threading;

	using Microsoft.Extensions.Logging.Abstractions;

	using NUnit.Framework;

	/// <summary>
	/// Runs the real server and session on a loopback port and talks to it in binary VLS encoding, the encoding a session
	/// starts in. The stores are fakes, so no test calls FRED.
	/// </summary>
	[TestFixture]
	[NonParallelizable]
	public class SessionTests
	{
		static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);
		static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

		// PLAN.md §7.3 item 4.
		[TestCase("fred-" + FakeStores.SeriesId)]
		public void SecurityDefinitionResponse_EchoesTheRequestedSymbol(string requestedSymbol)
		{
			using var connection = Connect(new FakeHistory(0));

			connection.Send(DtcWire.EncodeSecurityDefinitionForSymbolRequest(17, requestedSymbol));

			var (requestId, symbol) = DtcWire.DecodeSecurityDefinitionResponse(connection.ReadFrame());
			Assert.That(requestId, Is.EqualTo(17));
			Assert.That(symbol, Is.EqualTo(requestedSymbol));
			connection.AssertNothingMoreArrives();
		}

		[Test]
		public void SecurityDefinitionRequest_ForAnUnknownSymbol_IsRejected()
		{
			using var connection = Connect(new FakeHistory(0));

			connection.Send(DtcWire.EncodeSecurityDefinitionForSymbolRequest(18, "fred-UNKNOWN"));

			var frame = connection.ReadFrame();
			Assert.That(DtcWire.TypeOf(frame), Is.EqualTo(DtcWire.SecurityDefinitionRejectType));
			Assert.That(BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(8)), Is.EqualTo(18));
		}

		// PLAN.md §7.3 items 1–3 through the session: the acceptance sizes, including exact multiples of 922.
		[TestCase(0)]
		[TestCase(1)]
		[TestCase(922)]
		[TestCase(923)]
		[TestCase(1844)]
		public void HistoryResponse_HasOneHeaderAndEndsWithOneFinalRecord(int recordsCount)
		{
			var history = new FakeHistory(recordsCount);
			using var connection = Connect(history);

			connection.Send(DtcWire.EncodeHistoricalPriceDataRequest(23, "fred-" + FakeStores.SeriesId, 0, 0));

			var response = DtcWire.DecodeHistoryResponse(connection.ReadHistoryResponse());
			connection.AssertNothingMoreArrives();
			Assert.That(response.Header.RequestId, Is.EqualTo(23));
			Assert.That(response.Header.NoRecordsToReturn, Is.EqualTo(recordsCount == 0 ? 1 : 0));
			Assert.That(response.Records, Has.Count.EqualTo(recordsCount));
			Assert.That(response.Records.Select(r => r.RequestId), Is.All.EqualTo(23));
			Assert.That(
				response.Records.Select(r => r.StartDateTime),
				Is.EqualTo(Enumerable.Range(0, recordsCount).Select(i => (long)(FakeHistory.DateOf(i) - DateTime.UnixEpoch).TotalDays * 86_400_000_000L)));
		}

		static Connection Connect(FakeHistory history)
		{
			var stores = new FakeStores(history);
			// Port 0: the OS picks a free port when the server binds, and the server's Endpoint reports it.
			var server = new ConnectionTrackingServer(stores);
			Assert.That(server.Start(), Is.True, "The server did not start.");
			TcpClient? client = null;
			try
			{
				var port = ((IPEndPoint)server.Endpoint).Port;
				Assert.That(port, Is.Not.Zero, "The server did not report the port it bound.");
				client = new TcpClient();
				client.Connect(IPAddress.Loopback, port);
				client.ReceiveTimeout = (int)ResponseTimeout.TotalMilliseconds;
				// NetCoreServer starts receiving before it calls Server.OnConnected. Wait for that callback so each test
				// starts from a fully accepted connection; ImmediateDisconnectTests covers clients that close before it.
				Assert.That(server.SessionConnected.Wait(ResponseTimeout), Is.True, "The server did not finish accepting the connection.");
				return new Connection(server, client);
			}
			catch
			{
				client?.Dispose();
				server.Stop();
				server.Dispose();
				throw;
			}
		}

		sealed class ConnectionTrackingServer : Server
		{
			public ConnectionTrackingServer(FakeStores stores)
				: base(IPAddress.Loopback, 0, true, stores, stores, NullLoggerFactory.Instance)
			{
			}

			public ManualResetEventSlim SessionConnected { get; } = new();

			protected override void OnConnected(NetCoreServer.TcpSession session)
			{
				base.OnConnected(session);
				SessionConnected.Set();
			}
		}

		sealed class Connection : IDisposable
		{
			readonly Server _server;
			readonly TcpClient _client;
			readonly NetworkStream _stream;

			public Connection(Server server, TcpClient client)
			{
				_server = server;
				_client = client;
				_stream = client.GetStream();
			}

			public void Send(byte[] message) => _stream.Write(message);

			public byte[] ReadFrame()
			{
				var header = new byte[4];
				_stream.ReadExactly(header);
				var size = BinaryPrimitives.ReadUInt16LittleEndian(header);
				Assert.That(size, Is.GreaterThanOrEqualTo(4));
				var frame = new byte[size];
				header.CopyTo(frame, 0);
				_stream.ReadExactly(frame, 4, size - 4);
				return frame;
			}

			/// <summary>Reads frames up to the end of a history response: a header with NoRecordsToReturn, or a final record.</summary>
			public List<byte[]> ReadHistoryResponse()
			{
				var frames = new List<byte[]>();
				while (true)
				{
					var frame = ReadFrame();
					frames.Add(frame);
					var type = DtcWire.TypeOf(frame);
					if (type == DtcWire.HistoricalPriceDataResponseHeaderType && DtcWire.DecodeHistoryHeader(frame).NoRecordsToReturn != 0)
						return frames;
					if (type == DtcWire.HistoricalPriceDataRecordResponseType && DtcWire.DecodeHistoryRecord(frame).IsFinalRecord != 0)
						return frames;
				}
			}

			/// <summary>Checks that the server sends nothing more, such as a repeated header or a second final record.</summary>
			public void AssertNothingMoreArrives()
			{
				Thread.Sleep(QuietPeriod);
				Assert.That(_client.Available, Is.EqualTo(0), "The server sent more bytes after the response ended.");
			}

			public void Dispose()
			{
				_stream.Dispose();
				_client.Dispose();
				_server.Stop();
				_server.Dispose();
			}
		}
	}
}
