// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer
{
	using System;
	using System.Buffers.Binary;
	using System.Context;
	using System.IO;
	using System.Net;
	using System.Net.Sockets;
	using System.Threading;
	using System.Threading.Tasks;

	using Dtc.Protocol;
	using Dtc.Protocol.Private;

	using Microsoft.Extensions.Logging;

	using NBLib.CodeFlow;
	using NBLib.Exceptions;
	using NBLib.Logging;

	using NetCoreServer;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DtcProtocolServer.Data;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol;

	using EncodeSecurityDefinitionResponseArgs = SomeDataProvider.DtcProtocolServer.DtcProtocol.IMessageEncoder.EncodeSecurityDefinitionResponseArgs;

	partial class Session : TcpSession
	{
		const int HistoryDownloadBatchSize = 922; // 85000 - goes to LOH, need less. 88 bytes per record.

		readonly bool _onlyHistoryServer;
		readonly ISymbolsStoreProvider _symbolsStoreProvider;
		readonly ISymbolHistoryStoreProvider _symbolHistoryStoreProvider;
		readonly ISeriesSource _seriesSource;
		readonly CancellationTokenSource _disconnectTokenSource = new ();
		MessageProtocol _currentMessageProtocol = MessageProtocol.CreateMessageProtocol(MessageProtocol.PreferredEncoding);

		long _requestId;
		int _processingReceivedCounter;

		public Session(
			Server server,
			bool onlyHistoryServer,
			ISymbolsStoreProvider symbolsStoreProvider,
			ISymbolHistoryStoreProvider symbolHistoryStoreProvider,
			ISeriesSource seriesSource,
			ILoggerFactory loggerFactory)
			: base(server)
		{
			_currentMessageProtocol.MessageStreamer.MessageBytesReceived += OnMessageReceived;
			_onlyHistoryServer = onlyHistoryServer;
			_symbolsStoreProvider = symbolsStoreProvider;
			_symbolHistoryStoreProvider = symbolHistoryStoreProvider;
			_seriesSource = seriesSource;
			_timeProvider = server.TimeProvider;
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
			StopSession();
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
			StopSession();
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
				CloseSession();
			}
			catch (Exception ex)
			{
				L.LogException(ex, "Error while handling received bytes; closing the connection.");
				CloseSession();
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
			var isLogoff = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span.Slice(2)) == DtcConstants.LOGOFF;
			lock (_sessionLock)
			{
				if (_closing) return;
				_lastMessageTimestamp = _timeProvider.GetTimestamp();
				_lossTimer?.Change(_lossTimeout, Timeout.InfiniteTimeSpan);
				if (isLogoff)
				{
					BeginClose();
					L.LogInformation("Client logged off; closing connection: {remoteEndPoint}", RemoteEndPoint);
				}
			}
			if (isLogoff)
			{
				CancelAndDisconnect();
				return;
			}
			// The message can be a slice of NetCoreServer's receive buffer, which the next read overwrites as soon as
			// OnReceived returns, while the processing below is not awaited. So the processing gets its own copy.
#pragma warning disable CS4014
			ProcessMessageAsync(buffer.ToArray());
#pragma warning restore CS4014
		}

		async Task ProcessMessageAsync(Memory<byte> buffer)
		{
			var requestId = Interlocked.Increment(ref _requestId);
			using var tcpRequestIdContext = RunContext.WithValue("TcpRequestId", requestId);
			using (RunContext.WithCancel(_disconnectTokenSource.Token, "ClientDisconnected", CancellationReason.OperationAborted))
			{
				try
				{
					((Server)Server).AddRequestProcessing();
					await L.LogOperationAsync(async () =>
					{
						var ct = GetContext.CancellationToken;
						ct.ThrowIfCancellationRequested();
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
								case DtcConstants.SYMBOL_SEARCH_REQUEST:
									await ProcessSymbolSearchRequestAsync(decoder, encoder, ct);
									break;
								// The series extension's private messages exist only in binary VLS, the encoding of every session.
								case DtcPrivateMessages.SeriesExtensionRequestType when IsVlsSession:
									ProcessSeriesExtensionRequest(decoder, encoder);
									break;
								case DtcPrivateMessages.SeriesMetadataRequestType when IsVlsSession:
									await ProcessSeriesMetadataRequestAsync(decoder, ct);
									break;
								case DtcPrivateMessages.SeriesVintagesRequestType when IsVlsSession:
									await ProcessSeriesVintagesRequestAsync(decoder, ct);
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
							SendResponse(encoder.GetEncodedMessage());
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
				SendResponse(encoder.GetEncodedMessage());
			}, "SendKnownSymbolsInformation()");
		}
	}
}