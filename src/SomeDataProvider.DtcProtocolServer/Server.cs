// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer
{
	using System;
	using System.Net;
	using System.Net.Sockets;
	using System.Threading;

	using Microsoft.Extensions.Logging;

	using NetCoreServer;

	using SomeDataProvider.DataStorage.Definitions;

	class Server : TcpServer
	{
		static readonly TimeSpan MaxWaitTimeForCurrentRequestToComplete = TimeSpan.FromSeconds(30);

		readonly ILoggerFactory _loggerFactory;
		readonly bool _onlyHistoryServer;
		readonly ISymbolsStore _symbolsStore;
		readonly ISymbolHistoryStoreProvider _historyStoreProvider;

		int _currentRequestsCount;

		public Server(
			IPAddress address,
			int port,
			bool onlyHistoryServer,
			ISymbolsStore symbolsStore,
			ISymbolHistoryStoreProvider historyStoreProvider,
			ILoggerFactory loggerFactory)
			: base(address, port)
		{
			_loggerFactory = loggerFactory;
			_onlyHistoryServer = onlyHistoryServer;
			_symbolsStore = symbolsStore;
			_historyStoreProvider = historyStoreProvider;
			L = loggerFactory.CreateLogger<Server>();
		}

		ILogger<Server> L { get; }

		public void AddRequestProcessing()
		{
			Interlocked.Increment(ref _currentRequestsCount);
		}

		public void RemoveRequestProcessing()
		{
			Interlocked.Decrement(ref _currentRequestsCount);
		}

		protected override TcpSession CreateSession()
		{
			return new Session(this, _onlyHistoryServer, _symbolsStore, _historyStoreProvider, _loggerFactory);
		}

		protected override void OnStarted()
		{
			L.LogInformation("Server started.");
		}

		protected override void OnStopped()
		{
			L.LogInformation("Server stopped.");
			L.LogInformation($"Waiting for current requests are over (max {MaxWaitTimeForCurrentRequestToComplete})...");
			SpinWait.SpinUntil(() => Interlocked.CompareExchange(ref _currentRequestsCount, 0, 0) == 0, MaxWaitTimeForCurrentRequestToComplete);
			L.LogInformation("Current requests are over. Can proceed with shutdown.");
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