// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer
{
	using System;
	using System.Context;
	using System.IO;
	using System.Net;
	using System.Net.Sockets;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Timers;

	using Dtc.Protocol;

	using Microsoft.Extensions.Logging;

	using NBLib.CodeFlow;
	using NBLib.Exceptions;
	using NBLib.Logging;

	using NetCoreServer;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol;

	using EncodeSecurityDefinitionResponseArgs = SomeDataProvider.DtcProtocolServer.DtcProtocol.IMessageEncoder.EncodeSecurityDefinitionResponseArgs;
	using Timer = System.Timers.Timer;

	// TcpSession.SendAsync is thread-safe, so use it instead of Send() and own synchronization for heartbeat timer.

	partial class Session : TcpSession
	{
		const int HistoryDownloadBatchSize = 922; // 85000 - goes to LOH, need less. 88 bytes per record.

		readonly bool _onlyHistoryServer;
		readonly ISymbolsStoreProvider _symbolsStoreProvider;
		readonly ISymbolHistoryStoreProvider _symbolHistoryStoreProvider;
		readonly CancellationTokenSource _disconnectTokenSource = new ();
		MessageProtocol _currentMessageProtocol = MessageProtocol.CreateMessageProtocol(MessageProtocol.PreferredEncoding);
		Timer? _heartbeatTimer;
		long _requestId;
		int _processingReceivedCounter;

		public Session(
			Server server,
			bool onlyHistoryServer,
			ISymbolsStoreProvider symbolsStoreProvider,
			ISymbolHistoryStoreProvider symbolHistoryStoreProvider,
			ILoggerFactory loggerFactory)
			: base(server)
		{
			_currentMessageProtocol.MessageStreamer.MessageBytesReceived += OnMessageReceived;
			_onlyHistoryServer = onlyHistoryServer;
			_symbolsStoreProvider = symbolsStoreProvider;
			_symbolHistoryStoreProvider = symbolHistoryStoreProvider;
			L = loggerFactory.CreateLogger<Session>();
		}

		/// <summary>
		/// The client's endpoint, captured while the session was connecting, or null if it was unavailable then. Unlike
		/// Socket.RemoteEndPoint it can be read after the client disconnected and the socket was disposed.
		/// </summary>
		internal EndPoint? RemoteEndPoint { get; private set; }

		ILogger<Session> L { get; }

		protected override void Dispose(bool disposingManagedResources)
		{
			if (!disposingManagedResources) return;
			StopHeartbeatTimer();
			_currentMessageProtocol.MessageStreamer.MessageBytesReceived -= OnMessageReceived;
			_currentMessageProtocol.Dispose();
			_disconnectTokenSource.Dispose();
		}

		protected override void OnConnecting()
		{
			// NetCoreServer calls this before it starts receiving, so the client's disconnect cannot have disposed the
			// socket yet; by the time it calls Server.OnConnected, it can have.
			RemoteEndPoint = SomeDataProvider.DtcProtocolServer.Server.TryGetRemoteEndPoint(Socket, L);
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
			// An exception that leaves OnReceived is unhandled on a socket thread and ends the process, so end just this
			// connection. After an invalid frame the stream's frame boundaries are unknown, so it cannot continue anyway.
			catch (InvalidDataException ex)
			{
				L.LogException(ex, LogLevel.Warning, "Invalid message frame received; closing the connection.");
				Disconnect();
			}
			catch (Exception ex)
			{
				L.LogException(ex, "Error while handling received bytes; closing the connection.");
				Disconnect();
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
			// The message can be a slice of NetCoreServer's receive buffer, which the next read overwrites as soon as
			// OnReceived returns, while the processing below is not awaited. So the processing gets its own copy.
#pragma warning disable CS4014
			ProcessMessageAsync(buffer.ToArray());
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
							L.LogInformation("RequestReceived: {requestType}", MessageTypeNames.Get(messageType));
							switch (messageType)
							{
								case DtcConstants.ENCODING_REQUEST:
									ProcessEncodingRequest(decoder, encoder);
									break;
								case DtcConstants.LOGON_REQUEST:
									ProcessLogonRequest(decoder, encoder);
									//// await SendKnownSymbolsInformationAsync(ct);
									break;
								case DtcConstants.HEARTBEAT:
									// TODO: Add Heartbeat detection logic.
									// It is recommended that if there is a loss of HEARTBEAT messages from the other side, for twice the amount of the HeartbeatIntervalInSeconds time that it is safe to assume that the other side is no longer present and the network socket should be then gracefully closed.
									break;
								case DtcConstants.HISTORICAL_PRICE_DATA_REQUEST:
									await ProcessHistoricalPriceDataRequestAsync(decoder, encoder, ct);
									break;
								case DtcConstants.MARKET_DATA_REQUEST:
									await ProcessMarketDataRequestAsync(decoder, encoder, ct);
									break;
								case DtcConstants.SECURITY_DEFINITION_FOR_SYMBOL_REQUEST:
									await ProcessSecurityDefinitionForSymbolRequestAsync(decoder, encoder, ct);
									break;
								default:
									L.LogWarning($"Message type is not supported: {MessageTypeNames.Get(messageType)}.");
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
		async Task SendKnownSymbolsInformationAsync(CancellationToken ct)
		{
			await L.LogOperationAsync(async () =>
			{
				var symbols = await _symbolsStoreProvider.GetKnownSymbolsAsync(ct);
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
			}, "SendKnownSymbolsInformation()");
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