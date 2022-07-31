// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer
{
	using System;
	using System.Context;
	using System.Net.Sockets;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Timers;

	using Microsoft.Extensions.Logging;

	using NBLib.CodeFlow;
	using NBLib.Exceptions;
	using NBLib.Logging;

	using NetCoreServer;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	using EncodeSecurityDefinitionResponseArgs = SomeDataProvider.DtcProtocolServer.DtcProtocol.IMessageEncoder.EncodeSecurityDefinitionResponseArgs;
	using Timer = System.Timers.Timer;

	// TcpSession.SendAsync is thread-safe, so use it instead of Send() and own synchronization for heartbeat timer.

	partial class Session : TcpSession
	{
		const int HistoryDownloadBatchSize = 922; // 85000 - goes to LOH, need less. 88 bytes per record.

		readonly bool _onlyHistoryServer;
		readonly ISymbolsStore _symbolsStore;
		readonly ISymbolHistoryStoreProvider _symbolHistoryStoreProvider;
		readonly CancellationTokenSource _disconnectTokenSource = new ();
		MessageProtocol _currentMessageProtocol = MessageProtocol.CreateMessageProtocol(MessageProtocol.PreferredEncoding);
		Timer? _heartbeatTimer;
		long _requestId;
		int _processingReceivedCounter;

		public Session(
			Server server,
			bool onlyHistoryServer,
			ISymbolsStore symbolsStore,
			ISymbolHistoryStoreProvider symbolHistoryStoreProvider,
			ILoggerFactory loggerFactory)
			: base(server)
		{
			_currentMessageProtocol.MessageStreamer.MessageBytesReceived += OnMessageReceived;
			_onlyHistoryServer = onlyHistoryServer;
			_symbolsStore = symbolsStore;
			_symbolHistoryStoreProvider = symbolHistoryStoreProvider;
			L = loggerFactory.CreateLogger<Session>();
		}

		ILogger<Session> L { get; }

		protected override void Dispose(bool disposingManagedResources)
		{
			if (!disposingManagedResources) return;
			StopHeartbeatTimer();
			_currentMessageProtocol.MessageStreamer.MessageBytesReceived -= OnMessageReceived;
			_currentMessageProtocol.Dispose();
			_disconnectTokenSource.Dispose();
		}

		protected override void OnSent(long sent, long pending)
		{
			L.LogDebug("BytesSent: {sent}/{pending}", sent, pending);
		}

		protected override void OnDisconnected()
		{
			StopHeartbeatTimer();
			_disconnectTokenSource.Cancel();
		}

		protected override void OnReceived(byte[] buffer, long offset, long size)
		{
			L.LogDebug("BytesReceived: {bytesReceived}/{offset}", size, offset);
			if (Interlocked.CompareExchange(ref _processingReceivedCounter, 1, 0) > 0)
				throw new InvalidOperationException("Parallel call of OnReceived() is not allowed.");
			try
			{
				_currentMessageProtocol.MessageStreamer.PutReceivedBytes(buffer.AsMemory(Convert.ToInt32(offset), Convert.ToInt32(size)));
			}
			finally
			{
				Interlocked.Decrement(ref _processingReceivedCounter);
			}
		}

		protected override void OnError(SocketError error)
		{
			base.OnError(error);
			L.LogError($"Socket error: {error}");
		}

		void OnMessageReceived(Memory<byte> buffer)
		{
#pragma warning disable CS4014
			ProcessMessageAsync(buffer);
#pragma warning restore CS4014
		}

		async Task ProcessMessageAsync(Memory<byte> buffer)
		{
			Interlocked.Increment(ref _requestId);
			using var tcpRequestIdContext = RunContext.WithValue("TcpRequestId", _requestId);
			using (RunContext.WithCancel(_disconnectTokenSource.Token, "ClientDisconnected", CancellationReason.OperationAborted))
			{
				try
				{
					((Server)Server).AddRequestProcessing();
					await L.LogOperationAsync(async () =>
					{
						var ct = GetContext.CancellationToken;
						var decoder = _currentMessageProtocol.MessageDecoderFactory.CreateMessageDecoder(buffer);
						var encoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
						await using (new FinallyAsync(() => decoder.TryDisposeAsync()))
						await using (new FinallyAsync(() => encoder.TryDisposeAsync()))
						{
							var messageType = decoder.DecodeMessageType();
							L.LogInformation("RequestReceived: {requestType}", messageType);
							switch (messageType)
							{
								case MessageTypeEnum.EncodingRequest:
									ProcessEncodingRequest(decoder, encoder);
									break;
								case MessageTypeEnum.LogonRequest:
									ProcessLogonRequest(decoder, encoder);
									//// await SendKnownSymbolsInformationAsync(ct);
									break;
								case MessageTypeEnum.Heartbeat:
									// TODO: Add Heartbeat detection logic.
									// It is recommended that if there is a loss of HEARTBEAT messages from the other side, for twice the amount of the HeartbeatIntervalInSeconds time that it is safe to assume that the other side is no longer present and the network socket should be then gracefully closed.
									break;
								case MessageTypeEnum.HistoricalPriceDataRequest:
									await ProcessHistoricalPriceDataRequestAsync(decoder, encoder, ct);
									break;
								case MessageTypeEnum.MarketDataRequest:
									await ProcessMarketDataRequestAsync(decoder, encoder, ct);
									break;
								case MessageTypeEnum.SecurityDefinitionForSymbolRequest:
									await ProcessSecurityDefinitionForSymbolRequestAsync(decoder, encoder, ct);
									break;
								default:
									L.LogWarning($"Message type is not supported: {messageType}.");
									break;
								//// throw new NotSupportedException($"Message type is not supported: {messageType}.");
							}
						}
					}, "ProcessRequest");
				}
				catch (Exception ex) when (ex.IsExplainedCancellation())
				{
					L.LogException(ex, LogLevel.Warning, "Request execution was interrupted.");
				}
				catch (Exception ex)
				{
					L.LogException(ex, "Error while processing request.");
					// TODO: Need to answer smth?
				}
				finally
				{
					((Server)Server).RemoveRequestProcessing();
				}
			}
		}

		// https://www.sierrachart.com/index.php?page=doc/DTC_TestClient.php#PopulatingSymbolList
		// ReSharper disable once UnusedMember.Local
		async ValueTask SendKnownSymbolsInformationAsync(CancellationToken ct)
		{
			// TODO: Logging.
			var symbols = await _symbolsStore.GetKnownSymbolsAsync(ct);
			var ln = symbols.Count;
			if (ln == 0) return;
			var encoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
			try
			{
				var i = 0;
				foreach (var symbol in symbols)
				{
					if (encoder.EncodedMessageSize >= 80_000)
					{
						SendAsync(encoder.GetEncodedMessage());
						await encoder.TryDisposeAsync();
						encoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
					}
					encoder.EncodeSecurityDefinitionResponse(new EncodeSecurityDefinitionResponseArgs(0, symbol, i == ln - 1));
					i++;
				}
			}
			finally
			{
				await encoder.TryDisposeAsync();
			}
			SendAsync(encoder.GetEncodedMessage());
		}

		void OnHeartbeatTimerElapsed(object? sender, ElapsedEventArgs e)
		{
			L.LogOperation(() =>
			{
				if (IsDisposed || !IsConnected)
				{
					L.LogDebug("DisposedOrDisconnected");
					return;
				}
				var encoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
				using (new Finally(() => encoder.TryDispose()))
				{
					encoder.EncodeHeartbeatMessage(0);
					L.LogInformation("Sending hearbeat...");
					SendAsync(encoder.GetEncodedMessage());
					L.LogInformation("Sent hearbeat.");
				}
			}, "SendHeartbeat");
		}

		void StartHeartbeatTimer(int intervalMs)
		{
			if (_onlyHistoryServer) return;
			StopHeartbeatTimer();
			var t = new Timer(intervalMs);
			t.Elapsed += OnHeartbeatTimerElapsed;
			t.Start();
			L.LogInformation("HearbeatTimerStarted({intervalMs})", intervalMs);
			_heartbeatTimer = t;
		}

		void StopHeartbeatTimer()
		{
			if (_onlyHistoryServer) return;
			var t = _heartbeatTimer;
			if (t == null) return;
			_heartbeatTimer = null;
			t.Stop();
			t.Elapsed -= OnHeartbeatTimerElapsed;
			L.LogInformation("HearbeatTimerStopped");
			t.Dispose();
		}
	}
}