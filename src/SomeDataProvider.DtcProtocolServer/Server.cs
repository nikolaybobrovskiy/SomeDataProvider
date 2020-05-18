// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer
{
	using System.Net;
	using System.Net.Sockets;

	using Microsoft.Extensions.Logging;

	using NetCoreServer;

	using SomeDataProvider.DataStorage.Definitions;

	class Server : TcpServer
	{
		readonly ILoggerFactory _loggerFactory;
		readonly bool _onlyHistoryServer;
		readonly ISymbolsStore _symbolsStore;
		readonly ISymbolHistoryStoreInstanceFactory _historyStoreInstanceFactory;

		public Server(
			IPAddress address,
			int port,
			bool onlyHistoryServer,
			ISymbolsStore symbolsStore,
			ISymbolHistoryStoreInstanceFactory historyStoreInstanceFactory,
			ILoggerFactory loggerFactory)
			: base(address, port)
		{
			_loggerFactory = loggerFactory;
			_onlyHistoryServer = onlyHistoryServer;
			_symbolsStore = symbolsStore;
			_historyStoreInstanceFactory = historyStoreInstanceFactory;
			L = loggerFactory.CreateLogger<Server>();
		}

		ILogger<Server> L { get; }

		protected override TcpSession CreateSession()
		{
			return new Session(this, _onlyHistoryServer, _symbolsStore, _historyStoreInstanceFactory, _loggerFactory);
		}

		protected override void OnStarted()
		{
			L.LogInformation("Server started.");
		}

		protected override void OnStopped()
		{
			L.LogInformation("Server stopped.");
		}

		protected override void OnError(SocketError error)
		{
			L.LogError($"Socket error: {error}.");
		}

		protected override void OnConnected(TcpSession session)
		{
			L.LogInformation("Connected to server: {remoteEndPoint}, {sessionId}", session.Socket.RemoteEndPoint, session.Id);
		}

		protected override void OnDisconnected(TcpSession session)
		{
			L.LogInformation("Disconnected from server: {sessionId}", session.Id);
		}
	}
}