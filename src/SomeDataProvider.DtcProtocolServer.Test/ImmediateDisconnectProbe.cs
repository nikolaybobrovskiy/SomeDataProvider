// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Net;
	using System.Net.Sockets;
	using System.Threading;

	using TcpSession = NetCoreServer.TcpSession;

	/// <summary>
	/// Clients that disconnect before the server's OnConnected callback runs. NetCoreServer 6.2.0 starts receiving before
	/// it calls TcpServer.OnConnected, so the client's disconnect can dispose the session socket first. That callback runs
	/// on a socket I/O thread, where an exception terminates the process instead of failing a test, so these scenarios
	/// run in a child process (see <see cref="ImmediateDisconnectTests"/>): it prints <see cref="SuccessMarker"/> and
	/// exits with 0 only if it survived and its checks passed.
	/// </summary>
	static class ImmediateDisconnectProbe
	{
		public const string Argument = "--immediate-disconnect-probe";
		public const string SuccessMarker = "PROBE-OK";

		public const string GatedClose = "gated-close";
		public const string GatedReset = "gated-reset";
		public const string GatedSendClose = "gated-send-close";
		public const string ImmediateClose = "immediate-close";

		const string ConnectedTemplate = "Connected to server: {remoteEndPoint}, {sessionId}";
		const int ImmediateCloseConnections = 360;

		static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
		static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

		enum Ending
		{
			Close,
			Reset,
		}

		public static int Run(string scenario)
		{
			AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			{
				Console.Error.WriteLine($"UNHANDLED EXCEPTION: {e.ExceptionObject}");
				Console.Error.Flush();
			};
			try
			{
				switch (scenario)
				{
					case GatedClose:
						RunGated(Ending.Close, false);
						break;
					case GatedReset:
						RunGated(Ending.Reset, false);
						break;
					case GatedSendClose:
						RunGated(Ending.Close, true);
						break;
					case ImmediateClose:
						RunImmediateClose();
						break;
					default:
						Console.Error.WriteLine($"Unknown scenario: {scenario}.");
						return 2;
				}
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"PROBE-FAILED: {ex}");
				return 3;
			}
			Console.WriteLine($"{SuccessMarker} {scenario}");
			return 0;
		}

		/// <summary>
		/// Deterministic: the session holds NetCoreServer's accept path after it started receiving and before it calls
		/// Server.OnConnected, until the client's disconnect has disposed the socket. Server.OnConnected then runs with
		/// the disposed socket, the production race. The client closes once that hold begins, which is before the server
		/// callback, unlike the tests' ConnectionTrackingServer that waits for the callback to end.
		/// </summary>
		static void RunGated(Ending ending, bool sendRequestFirst)
		{
			var logs = new RecordingLoggerFactory();
			var server = new ProbeServer(logs, true);
			try
			{
				var port = Start(server);
				EndPoint clientEndPoint;
				using (var client = new TcpClient(AddressFamily.InterNetwork))
				{
					client.Connect(IPAddress.Loopback, port);
					clientEndPoint = client.Client.LocalEndPoint!;
					Require(server.GatedSessionCreated.Wait(Timeout), "The server did not accept the connection.");
					var session = server.GatedSession!;
					Require(session.EnteredOnConnected.Wait(Timeout), "The session did not reach OnConnected.");
					if (sendRequestFirst)
						client.GetStream().Write(DtcWire.EncodeSecurityDefinitionForSymbolRequest(1, "fred-" + FakeStores.SeriesId));
					End(client, ending);
				}
				var gated = server.GatedSession!;
				Require(server.WaitForServerOnConnected(1, Timeout), "Server.OnConnected did not finish.");
				Require(gated.DisconnectedBeforeServerOnConnected, "The client's disconnect did not complete before Server.OnConnected ran, so the race was not reproduced.");
				Require(server.SocketsDisposedAtServerOnConnected == 1, "The session socket was not disposed when Server.OnConnected ran.");

				var connected = logs.WithTemplate(ConnectedTemplate);
				Require(connected.Count == 1, $"Expected one connection log entry, found {connected.Count}.");
				var loggedEndPoint = connected[0].Values["remoteEndPoint"];
				Require(Equals(loggedEndPoint, clientEndPoint), $"The connection log has endpoint {loggedEndPoint?.ToString() ?? "null"}; the client is {clientEndPoint}.");
				Require(Equals(connected[0].Values["sessionId"], gated.Id), "The connection log has another session's id.");
				Console.WriteLine($"Logged: {connected[0].Message}");
			}
			finally
			{
				server.Stop();
				server.Dispose();
			}
		}

		/// <summary>
		/// Clients that close (or reset) right after they connect, some after sending a request, with no wait for any
		/// server callback: at once, after a short random spin, or after a millisecond, so that disconnects land at
		/// different points of NetCoreServer's accept path. Whether one lands just before Server.OnConnected varies from
		/// run to run; the gated scenarios reproduce that deterministically. This one checks that the process survives a
		/// burst of such clients and that the server callback still runs and logs for those it reaches.
		/// </summary>
		static void RunImmediateClose()
		{
			var logs = new RecordingLoggerFactory();
			var server = new ProbeServer(logs, false);
			var random = new Random(20261017);
			try
			{
				var port = Start(server);
				var request = DtcWire.EncodeSecurityDefinitionForSymbolRequest(1, "fred-" + FakeStores.SeriesId);
				for (var i = 0; i < ImmediateCloseConnections; i++)
				{
					using var client = new TcpClient(AddressFamily.InterNetwork);
					client.Connect(IPAddress.Loopback, port);
					if (i % 3 == 1)
						client.GetStream().Write(request);
					switch (i / 3 % 3)
					{
						case 1:
							Thread.SpinWait(random.Next(20_000));
							break;
						case 2:
							Thread.Sleep(1);
							break;
					}
					End(client, i % 3 == 2 ? Ending.Reset : Ending.Close);
				}

				// Every session disconnects. NetCoreServer skips OnConnected for a session it already sees disposed, so the
				// server callback runs once per session whose own OnConnected ran. Wait until those counts settle and stay
				// settled, so that no callback is still running when the process exits.
				bool Settled() =>
					server.DisconnectedCount == ImmediateCloseConnections
					&& server.SessionOnConnectedCount == server.ServerOnConnectedCount;
				Require(SpinWait.SpinUntil(Settled, Timeout), $"Connections did not settle: {Describe(server)}.");
				Thread.Sleep(QuietPeriod);
				Require(Settled(), $"Connections did not stay settled: {Describe(server)}.");
				Require(server.CreatedCount == ImmediateCloseConnections, $"Unexpected session count: {Describe(server)}.");
				Require(server.ServerOnConnectedCount > 0, $"No connection reached Server.OnConnected: {Describe(server)}.");
				var connectedLogs = logs.WithTemplate(ConnectedTemplate).Count;
				Require(connectedLogs == server.ServerOnConnectedCount, $"Expected {server.ServerOnConnectedCount} connection log entries, found {connectedLogs}.");
				Console.WriteLine(Describe(server));
			}
			finally
			{
				server.Stop();
				server.Dispose();
			}
		}

		static string Describe(ProbeServer server) =>
			$"sessions={server.CreatedCount}, disconnected={server.DisconnectedCount}, sessionOnConnected={server.SessionOnConnectedCount}, " +
			$"serverOnConnected={server.ServerOnConnectedCount}, socketDisposedAtServerOnConnected={server.SocketsDisposedAtServerOnConnected}";

		static int Start(ProbeServer server)
		{
			Require(server.Start(), "The server did not start.");
			var port = ((IPEndPoint)server.Endpoint).Port;
			Require(port != 0, "The server did not report the port it bound.");
			return port;
		}

		static void End(TcpClient client, Ending ending)
		{
			if (ending == Ending.Reset)
				client.Client.LingerState = new LingerOption(true, 0); // Close sends RST instead of FIN.
			client.Close();
		}

		static void Require(bool condition, string message)
		{
			if (!condition)
				throw new InvalidOperationException(message);
		}

		sealed class ProbeServer : Server
		{
			readonly FakeStores _stores;
			readonly RecordingLoggerFactory _logs;
			readonly bool _gate;
			int _createdCount;
			int _disconnectedCount;
			int _sessionOnConnectedCount;
			int _serverOnConnectedCount;
			int _socketsDisposedAtServerOnConnected;

			public ProbeServer(RecordingLoggerFactory logs, bool gate)
				: this(new FakeStores(new FakeHistory(0)), logs, gate)
			{
			}

			ProbeServer(FakeStores stores, RecordingLoggerFactory logs, bool gate)
				: base(IPAddress.Loopback, 0, true, stores, stores, stores, logs)
			{
				_stores = stores;
				_logs = logs;
				_gate = gate;
			}

			public ManualResetEventSlim GatedSessionCreated { get; } = new();

			public ProbeSession? GatedSession { get; private set; }

			public int CreatedCount => Volatile.Read(ref _createdCount);

			public int DisconnectedCount => Volatile.Read(ref _disconnectedCount);

			public int SessionOnConnectedCount => Volatile.Read(ref _sessionOnConnectedCount);

			public int ServerOnConnectedCount => Volatile.Read(ref _serverOnConnectedCount);

			public int SocketsDisposedAtServerOnConnected => Volatile.Read(ref _socketsDisposedAtServerOnConnected);

			public bool WaitForServerOnConnected(int count, TimeSpan timeout) => SpinWait.SpinUntil(() => ServerOnConnectedCount >= count, timeout);

			public void SessionReachedOnConnected() => Interlocked.Increment(ref _sessionOnConnectedCount);

			protected override TcpSession CreateSession()
			{
				var session = new ProbeSession(this, _gate, _stores, _logs);
				if (Interlocked.Increment(ref _createdCount) == 1 && _gate)
				{
					GatedSession = session;
					GatedSessionCreated.Set();
				}
				return session;
			}

			protected override void OnConnected(TcpSession session)
			{
				var socketDisposed = session.IsSocketDisposed;
				// No try/catch: an exception from the server's callback must escape here and terminate the probe.
				base.OnConnected(session);
				if (socketDisposed)
					Interlocked.Increment(ref _socketsDisposedAtServerOnConnected);
				Interlocked.Increment(ref _serverOnConnectedCount);
			}

			protected override void OnDisconnected(TcpSession session)
			{
				base.OnDisconnected(session);
				Interlocked.Increment(ref _disconnectedCount);
			}
		}

		sealed class ProbeSession : Session
		{
			readonly ProbeServer _server;
			readonly bool _gate;

			public ProbeSession(ProbeServer server, bool gate, FakeStores stores, RecordingLoggerFactory logs)
				: base(server, true, stores, stores, stores, logs)
			{
				_server = server;
				_gate = gate;
			}

			public ManualResetEventSlim EnteredOnConnected { get; } = new();

			public ManualResetEventSlim Disconnected { get; } = new();

			public bool DisconnectedBeforeServerOnConnected { get; private set; }

			// NetCoreServer calls the session's OnConnected right before the server's, after it started receiving.
			protected override void OnConnected()
			{
				base.OnConnected();
				_server.SessionReachedOnConnected();
				if (!_gate) return;
				EnteredOnConnected.Set();
				DisconnectedBeforeServerOnConnected = Disconnected.Wait(Timeout);
			}

			protected override void OnDisconnected()
			{
				base.OnDisconnected();
				Disconnected.Set();
			}
		}
	}
}
