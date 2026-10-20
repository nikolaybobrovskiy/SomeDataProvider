// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System.Linq;
	using System.Net;

	using Microsoft.Extensions.Logging;

	using NUnit.Framework;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol;

	/// <summary>
	/// NetCoreServer reuses its receive buffer for the next read as soon as Session.OnReceived returns, while the
	/// session goes on processing the request without awaiting it. These tests call OnReceived directly, without a
	/// socket, and overwrite the receive buffer while the request is being processed, as the next read would.
	/// </summary>
	[TestFixture]
	public class SessionReceiveBufferTests
	{
		const string RequestReceivedTemplate = "RequestReceived: {requestType}";
		const string SecurityDefinitionRequestTemplate = "SecurityDefinitionForSymbolRequest: {securityDefinitionRequest}";

		// NetCoreServer's default receive buffer size.
		const int ReceiveBufferSize = 8192;

		[Test]
		public void Request_IsDecodedAsSent_WhenTheReceiveBufferIsReusedBeforeDecoding([Values(0, 100)] int offset)
		{
			var sent = DtcWire.EncodeSecurityDefinitionForSymbolRequest(17, "fred-" + FakeStores.SeriesId);
			// The next read: a request of the same size, so a decoder that reads it gets a valid but different request.
			var nextRead = DtcWire.EncodeSecurityDefinitionForSymbolRequest(18, "fred-CPI");
			Assert.That(nextRead, Has.Length.EqualTo(sent.Length));
			var receiveBuffer = new byte[ReceiveBufferSize];
			sent.CopyTo(receiveBuffer, offset);
			var logs = new RecordingLoggerFactory();
			var overwrites = 0;
			// The session logs the message type after it starts processing the message and before it decodes the
			// request, so the buffer is overwritten where an await would let the next read in.
			logs.Logged = entry =>
			{
				if (entry.Template != RequestReceivedTemplate)
					return;
				overwrites++;
				nextRead.CopyTo(receiveBuffer, offset);
			};
			var stores = new FakeStores(new FakeHistory(0));
			// The server is never started: the session only needs it to count the requests in progress.
			using (var server = new Server(IPAddress.Loopback, 0, true, stores, stores, stores, logs))
			using (var session = new ReceivingSession(server, stores, logs))
			{
				// The fake stores complete at once, so the session processes the request before PassReceivedBytes returns.
				session.PassReceivedBytes(receiveBuffer, offset, sent.Length);
			}

			Assert.That(overwrites, Is.EqualTo(1), "The request was not processed exactly once.");
			var decoded = logs.WithTemplate(SecurityDefinitionRequestTemplate)
				.Select(e => (SecurityDefinitionForSymbolRequest?)e.Values["securityDefinitionRequest"])
				.ToList();
			Assert.That(decoded, Has.Count.EqualTo(1), "The request was not decoded exactly once.");
			Assert.That(decoded[0]!.RequestId, Is.EqualTo(17));
			Assert.That(decoded[0]!.Symbol, Is.EqualTo("fred-" + FakeStores.SeriesId));
			Assert.That(logs.Entries.Where(e => e.Level >= LogLevel.Warning), Is.Empty, "Processing logged a warning or an error.");
		}

		/// <summary>A session that the test passes received bytes to, as NetCoreServer does after a read.</summary>
		sealed class ReceivingSession : Session
		{
			public ReceivingSession(Server server, FakeStores stores, ILoggerFactory loggerFactory)
				: base(server, true, stores, stores, stores, loggerFactory)
			{
			}

			public void PassReceivedBytes(byte[] buffer, long offset, long size) => OnReceived(buffer, offset, size);
		}
	}
}
