// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Buffers.Binary;
	using System.Diagnostics;
	using System.IO;
	using System.Linq;
	using System.Net;
	using System.Net.Sockets;
	using System.Runtime.InteropServices;
	using System.Threading;

	using NUnit.Framework;

	/// <summary>
	/// A client that disconnects before NetCoreServer calls Server.OnConnected must not crash the server. The connection
	/// scenarios run in a child process, because an exception from that callback is thrown on a socket I/O thread and
	/// terminates the process: a crash there fails the test with the child's output instead of aborting the test run.
	/// </summary>
	[TestFixture]
	[NonParallelizable]
	public class ImmediateDisconnectTests
	{
		const string ConnectedTemplate = "Connected to server: {remoteEndPoint}, {sessionId}";

		static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(90);
		static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

		[TestCase(ImmediateDisconnectProbe.GatedClose)]
		[TestCase(ImmediateDisconnectProbe.GatedReset)]
		[TestCase(ImmediateDisconnectProbe.GatedSendClose)]
		[TestCase(ImmediateDisconnectProbe.ImmediateClose)]
		public void ServerSurvivesAClientThatDisconnectsBeforeOnConnected(string scenario)
		{
			var (exitCode, output, error) = RunProbe(scenario);

			var report = $"Probe exit code: {exitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{output}{Environment.NewLine}stderr:{Environment.NewLine}{error}";
			TestContext.Out.WriteLine(report);
			Assert.That(exitCode, Is.Zero, report);
			Assert.That(output, Does.Contain($"{ImmediateDisconnectProbe.SuccessMarker} {scenario}"), report);
		}

		[Test]
		public void NormalConnection_LogsTheClientEndPoint()
		{
			var logs = new RecordingLoggerFactory();
			var stores = new FakeStores(new FakeHistory(0));
			var server = new Server(IPAddress.Loopback, 0, true, stores, stores, stores, logs);
			Assert.That(server.Start(), Is.True, "The server did not start.");
			try
			{
				using var client = new TcpClient(AddressFamily.InterNetwork);
				client.Connect(IPAddress.Loopback, ((IPEndPoint)server.Endpoint).Port);

				Assert.That(SpinWait.SpinUntil(() => logs.WithTemplate(ConnectedTemplate).Count > 0, ResponseTimeout), Is.True, "The server did not log the connection.");
				var entry = logs.WithTemplate(ConnectedTemplate).Single();
				Assert.That(entry.Values["remoteEndPoint"], Is.EqualTo(client.Client.LocalEndPoint));
				Assert.That(entry.Values["sessionId"], Is.TypeOf<Guid>());

				// The connection still works after the callback.
				var stream = client.GetStream();
				stream.Write(DtcWire.EncodeSecurityDefinitionForSymbolRequest(5, "fred-" + FakeStores.SeriesId));
				client.ReceiveTimeout = (int)ResponseTimeout.TotalMilliseconds;
				var header = new byte[4];
				stream.ReadExactly(header);
				var frame = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header)];
				header.CopyTo(frame, 0);
				stream.ReadExactly(frame, 4, frame.Length - 4);
				Assert.That(DtcWire.DecodeSecurityDefinitionResponse(frame), Is.EqualTo((5, "fred-" + FakeStores.SeriesId)));
			}
			finally
			{
				server.Stop();
				server.Dispose();
			}
		}

		[Test]
		public void TryGetRemoteEndPoint_OfAConnectedSocket_IsThePeer()
		{
			var logs = new RecordingLoggerFactory();
			var (listener, client, accepted) = ConnectPair();
			using (listener)
			using (client)
			using (accepted)
				Assert.That(Server.TryGetRemoteEndPoint(accepted, logs.CreateLogger("Test")), Is.EqualTo(client.LocalEndPoint));
			Assert.That(logs.Entries, Is.Empty);
		}

		[Test]
		public void TryGetRemoteEndPoint_OfADisposedSocket_IsNull()
		{
			var logs = new RecordingLoggerFactory();
			var (listener, client, accepted) = ConnectPair();
			using (listener)
			using (client)
			{
				accepted.Dispose();
				Assert.That(() => accepted.RemoteEndPoint, Throws.TypeOf<ObjectDisposedException>(), "Precondition: the property throws for a disposed socket.");

				Assert.That(Server.TryGetRemoteEndPoint(accepted, logs.CreateLogger("Test")), Is.Null);
			}
			Assert.That(logs.Entries.Select(e => e.Message), Is.EqualTo(new[] { "RemoteEndPointUnavailable: the socket is disposed." }));
		}

		[Test]
		public void TryGetRemoteEndPoint_OfNoSocket_IsNull()
		{
			Assert.That(Server.TryGetRemoteEndPoint(null, new RecordingLoggerFactory().CreateLogger("Test")), Is.Null);
		}

		// A session the server has not connected has no socket; the callback reads the session's captured endpoint.
		[Test]
		public void OnConnected_ForASessionWithoutAnEndPoint_LogsWithoutThrowing()
		{
			var logs = new RecordingLoggerFactory();
			var stores = new FakeStores(new FakeHistory(0));
			using var server = new CallbackServer(stores, logs);
			using var session = new Session(server, true, stores, stores, stores, logs);
			using var plainSession = new NetCoreServer.TcpSession(server);

			Assert.That(() => server.CallOnConnected(session), Throws.Nothing);
			Assert.That(() => server.CallOnConnected(plainSession), Throws.Nothing);

			var entries = logs.WithTemplate(ConnectedTemplate);
			Assert.That(entries.Select(e => e.Values["remoteEndPoint"]), Is.EqualTo(new object?[] { null, null }));
			Assert.That(entries.Select(e => e.Values["sessionId"]), Is.EqualTo(new object[] { session.Id, plainSession.Id }));
		}

		static (Socket Listener, Socket Client, Socket Accepted) ConnectPair()
		{
			var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
			listener.Listen(1);
			var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			client.Connect(listener.LocalEndPoint!);
			return (listener, client, listener.Accept());
		}

		static (int ExitCode, string Output, string Error) RunProbe(string scenario)
		{
			var assemblyPath = typeof(ImmediateDisconnectProbe).Assembly.Location;
			var startInfo = new ProcessStartInfo(DotNetHost())
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
			};
			startInfo.ArgumentList.Add("exec");
			startInfo.ArgumentList.Add(assemblyPath);
			startInfo.ArgumentList.Add(ImmediateDisconnectProbe.Argument);
			startInfo.ArgumentList.Add(scenario);

			using var process = Process.Start(startInfo)!;
			var output = process.StandardOutput.ReadToEndAsync();
			var error = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(ProbeTimeout))
			{
				process.Kill(true);
				process.WaitForExit();
				Assert.Fail($"The probe did not finish within {ProbeTimeout}.{Environment.NewLine}stdout:{Environment.NewLine}{output.Result}{Environment.NewLine}stderr:{Environment.NewLine}{error.Result}");
			}
			process.WaitForExit(); // Lets the redirected streams finish.
			return (process.ExitCode, output.Result, error.Result);
		}

		/// <summary>The dotnet host running the tests: the one dotnet test reports, or the one that owns this runtime.</summary>
		static string DotNetHost()
		{
			var hostName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
			var fromCli = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
			if (!string.IsNullOrEmpty(fromCli) && File.Exists(fromCli))
				return fromCli;
			// The runtime directory is <dotnet root>/shared/Microsoft.NETCore.App/<version>/.
			var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
			var root = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
			var host = Path.Combine(root, hostName);
			return File.Exists(host) ? host : hostName;
		}

		/// <summary>Lets a test call the server's connection callback directly.</summary>
		sealed class CallbackServer : Server
		{
			public CallbackServer(FakeStores stores, RecordingLoggerFactory logs)
				: base(IPAddress.Loopback, 0, true, stores, stores, stores, logs)
			{
			}

			public void CallOnConnected(NetCoreServer.TcpSession session) => OnConnected(session);
		}
	}
}
