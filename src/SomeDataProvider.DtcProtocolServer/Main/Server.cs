namespace SomeDataProvider.DtcProtocolServer.Main
{
	using System.Net;
	using System.Net.Sockets;

	using Microsoft.Extensions.Logging;

	using NetCoreServer;

	class Server : TcpServer
	{
		public Server(IPAddress address, int port, ILoggerFactory loggerFactory)
			: base(address, port)
		{
			L = loggerFactory.CreateLogger<Server>();
		}

		ILogger<Server> L { get; }

		protected override TcpSession CreateSession()
		{
			return new Session(this);
		}

		protected override void OnStarted()
		{
			base.OnStarted();
			L.LogInformation("Server started.");
		}

		protected override void OnStopped()
		{
			base.OnStopped();
			L.LogInformation("Server stopped.");
		}

		protected override void OnError(SocketError error)
		{
			L.LogError($"Socket error: {error}.");
			base.OnError(error);
		}
	}
}