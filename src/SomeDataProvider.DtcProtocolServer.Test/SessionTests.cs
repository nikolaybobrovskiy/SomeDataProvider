// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Buffers.Binary;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Net;
	using System.Net.Sockets;
	using System.Threading;

	using Dtc.Protocol;

	using Microsoft.Extensions.Logging;
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

		// PLAN.md §7.3 item 7. FakeStores' catalog: GDP/"Gross Domestic Product", GDPC1/"Real Gross Domestic
		// Product", UNRATE/"Unemployment Rate".
		[Test]
		public void SymbolSearchResponse_WithMatches_HasTheFinalFlagOnlyOnTheLast()
		{
			using var connection = Connect(new FakeHistory(0));

			connection.Send(DtcWire.EncodeSymbolSearchRequest(30, "Product", searchType: SearchTypeEnum.SEARCH_TYPE_BY_DESCRIPTION));

			var frames = new[] { connection.ReadFrame(), connection.ReadFrame() };
			connection.AssertNothingMoreArrives();

			Assert.That(frames.Select(DtcWire.TypeOf), Is.All.EqualTo(DtcWire.SecurityDefinitionResponseType));
			Assert.That(frames.Select(f => DtcWire.DecodeSecurityDefinitionResponse(f).RequestId), Is.All.EqualTo(30));
			Assert.That(frames.Select(DtcWire.DecodeSecurityDefinitionResponseIsFinalMessage), Is.EqualTo(new[] { false, true }));
		}

		[Test]
		public void SymbolSearchResponse_WithNoMatches_IsOneResponseWithOnlyRequestIdAndTheFinalFlag()
		{
			using var connection = Connect(new FakeHistory(0));

			connection.Send(DtcWire.EncodeSymbolSearchRequest(31, "NoSuchSeries"));

			var frame = connection.ReadFrame();
			connection.AssertNothingMoreArrives();
			var (requestId, symbol) = DtcWire.DecodeSecurityDefinitionResponse(frame);
			Assert.That(requestId, Is.EqualTo(31));
			Assert.That(symbol, Is.Empty);
			Assert.That(DtcWire.DecodeSecurityDefinitionResponseIsFinalMessage(frame), Is.True);
		}

		[Test]
		public void SymbolSearchRequest_WithEmptySearchText_IsRejectedWithoutCallingTheStore()
		{
			var stores = new FakeStores(new FakeHistory(0));
			using var connection = Connect(stores);

			connection.Send(DtcWire.EncodeSymbolSearchRequest(32, string.Empty));

			var frame = connection.ReadFrame();
			Assert.That(DtcWire.TypeOf(frame), Is.EqualTo(DtcWire.SecurityDefinitionRejectType));
			Assert.That(BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(8)), Is.EqualTo(32));
			Assert.That(stores.LastSearchLimit, Is.Null, "The store was called for an empty SearchText.");
		}

		[Test]
		public void SymbolSearchRequest_RequestsNoMoreThanTheDocumentedCap()
		{
			var stores = new FakeStores(new FakeHistory(0));
			using var connection = Connect(stores);

			connection.Send(DtcWire.EncodeSymbolSearchRequest(33, "Product"));

			connection.ReadFrame();
			connection.ReadFrame();
			connection.AssertNothingMoreArrives();
			Assert.That(stores.LastSearchLimit, Is.EqualTo(Session.MaxSymbolSearchResults));
		}

		[Test]
		public void SymbolSearchRequest_OnStoreFailure_IsRejected()
		{
			using var connection = Connect(new FakeHistory(0));

			connection.Send(DtcWire.EncodeSymbolSearchRequest(34, FakeStores.FailingSearchText));

			var frame = connection.ReadFrame();
			Assert.That(DtcWire.TypeOf(frame), Is.EqualTo(DtcWire.SecurityDefinitionRejectType));
			Assert.That(BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(8)), Is.EqualTo(34));
		}

		[TestCase(SearchTypeEnum.SEARCH_TYPE_BY_SYMBOL, "GDPC1")]
		[TestCase(SearchTypeEnum.SEARCH_TYPE_BY_DESCRIPTION, "Real Gross")]
		public void SymbolSearchRequest_SupportsBothSearchTypes(SearchTypeEnum searchType, string searchText)
		{
			using var connection = Connect(new FakeHistory(0));

			connection.Send(DtcWire.EncodeSymbolSearchRequest(35, searchText, searchType: searchType));

			var frame = connection.ReadFrame();
			connection.AssertNothingMoreArrives();
			var (requestId, symbol) = DtcWire.DecodeSecurityDefinitionResponse(frame);
			Assert.That(requestId, Is.EqualTo(35));
			Assert.That(symbol, Is.EqualTo("GDPC1"));
			Assert.That(DtcWire.DecodeSecurityDefinitionResponseIsFinalMessage(frame), Is.True);
		}

		// Per the DTC spec, a request that constrains Exchange or SecurityType matches on those too, not SearchText
		// alone. Profile v1 symbols never carry an Exchange or a SecurityType other than unset, so such a request
		// cannot match anything, even though its SearchText alone would.
		[Test]
		public void SymbolSearchRequest_WithExchangeOrSecurityTypeConstraint_IsNoMatches()
		{
			using var connection = Connect(new FakeHistory(0));

			connection.Send(DtcWire.EncodeSymbolSearchRequest(
				36,
				"GDPC1",
				exchange: "NYSE",
				securityType: SecurityTypeEnum.SECURITY_TYPE_STOCK,
				searchType: SearchTypeEnum.SEARCH_TYPE_BY_SYMBOL));

			var frame = connection.ReadFrame();
			var (requestId, symbol) = DtcWire.DecodeSecurityDefinitionResponse(frame);
			Assert.That(requestId, Is.EqualTo(36));
			Assert.That(symbol, Is.Empty);
			Assert.That(DtcWire.DecodeSecurityDefinitionResponseIsFinalMessage(frame), Is.True);
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

		// A read that ends inside the Size field used to throw from OnReceived, on a socket thread.
		[TestCase(1)]
		[TestCase(2)]
		[TestCase(3)]
		public void Request_SplitInsideItsHeader_IsAnswered(int splitAt)
		{
			using var connection = Connect(new FakeHistory(0));

			connection.SendInTwoParts(DtcWire.EncodeSecurityDefinitionForSymbolRequest(19, "fred-" + FakeStores.SeriesId), splitAt);

			var (requestId, _) = DtcWire.DecodeSecurityDefinitionResponse(connection.ReadFrame());
			Assert.That(requestId, Is.EqualTo(19));
		}

		// Size 0 used to recurse until a stack overflow ended the server process.
		[Test]
		public void SizeBelowHeader_ClosesOnlyThatConnection([Values(0, 1, 2, 3)] int size)
		{
			var logs = new RecordingLoggerFactory();
			using var connection = Connect(new FakeHistory(0), logs);
			var heartbeatHeader = new byte[4];
			BinaryPrimitives.WriteUInt16LittleEndian(heartbeatHeader, (ushort)size);
			BinaryPrimitives.WriteUInt16LittleEndian(heartbeatHeader.AsSpan(2), 3);

			connection.Send(heartbeatHeader);

			connection.AssertClosedByServer();
			Assert.That(
				logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Exception is InvalidDataException),
				Has.Exactly(1).Items,
				"The invalid frame was not logged once as a warning.");
			using var nextConnection = connection.ConnectAnotherClient();
			nextConnection.Send(DtcWire.EncodeSecurityDefinitionForSymbolRequest(21, "fred-" + FakeStores.SeriesId));
			var (requestId, _) = DtcWire.DecodeSecurityDefinitionResponse(nextConnection.ReadFrame());
			Assert.That(requestId, Is.EqualTo(21), "The server stopped serving other connections.");
		}

		static Connection Connect(FakeHistory history, ILoggerFactory? loggerFactory = null) =>
			Connect(new FakeStores(history), loggerFactory);

		static Connection Connect(FakeStores stores, ILoggerFactory? loggerFactory = null)
		{
			// Port 0: the OS picks a free port when the server binds, and the server's Endpoint reports it.
			var server = new ConnectionTrackingServer(stores, loggerFactory ?? NullLoggerFactory.Instance);
			Assert.That(server.Start(), Is.True, "The server did not start.");
			TcpClient? client = null;
			try
			{
				client = ConnectClient(server);
				return new Connection(server, client, true);
			}
			catch
			{
				client?.Dispose();
				server.Stop();
				server.Dispose();
				throw;
			}
		}

		static TcpClient ConnectClient(ConnectionTrackingServer server)
		{
			var port = ((IPEndPoint)server.Endpoint).Port;
			Assert.That(port, Is.Not.Zero, "The server did not report the port it bound.");
			server.SessionConnected.Reset();
			var client = new TcpClient { NoDelay = true };
			try
			{
				client.Connect(IPAddress.Loopback, port);
				client.ReceiveTimeout = (int)ResponseTimeout.TotalMilliseconds;
				// NetCoreServer starts receiving before it calls Server.OnConnected. Wait for that callback so each test
				// starts from a fully accepted connection; ImmediateDisconnectTests covers clients that close before it.
				Assert.That(server.SessionConnected.Wait(ResponseTimeout), Is.True, "The server did not finish accepting the connection.");
				return client;
			}
			catch
			{
				client.Dispose();
				throw;
			}
		}

		sealed class ConnectionTrackingServer : Server
		{
			public ConnectionTrackingServer(FakeStores stores, ILoggerFactory loggerFactory)
				: base(IPAddress.Loopback, 0, true, stores, stores, loggerFactory)
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
			readonly ConnectionTrackingServer _server;
			readonly TcpClient _client;
			readonly NetworkStream _stream;
			readonly bool _ownsServer;

			public Connection(ConnectionTrackingServer server, TcpClient client, bool ownsServer)
			{
				_server = server;
				_client = client;
				_stream = client.GetStream();
				_ownsServer = ownsServer;
			}

			public void Send(byte[] message) => _stream.Write(message);

			/// <summary>Sends a message in two writes, with a pause between them so the server receives them separately.</summary>
			public void SendInTwoParts(byte[] message, int splitAt)
			{
				_stream.Write(message, 0, splitAt);
				Thread.Sleep(QuietPeriod);
				_stream.Write(message, splitAt, message.Length - splitAt);
			}

			/// <summary>Opens another client connection to the same server; disposing it leaves the server running.</summary>
			public Connection ConnectAnotherClient() => new(_server, ConnectClient(_server), false);

			/// <summary>Checks that the server closes the connection without sending anything first.</summary>
			public void AssertClosedByServer()
			{
				int read;
				try
				{
					read = _stream.Read(new byte[1]);
				}
				catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
				{
					return;
				}
				// A receive timeout throws IOException with SocketError.TimedOut, which fails the test.
				Assert.That(read, Is.Zero, "The server sent bytes instead of closing the connection.");
			}

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
				if (!_ownsServer)
					return;
				_server.Stop();
				_server.Dispose();
			}
		}
	}
}
