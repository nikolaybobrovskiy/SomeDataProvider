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

	using NBLib.BuiltInTypes;
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

	class Session : TcpSession
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

		async ValueTask ProcessSecurityDefinitionForSymbolRequestAsync(IMessageDecoder decoder, IMessageEncoder encoder, CancellationToken ct)
		{
			var securityDefinitionRequest = decoder.DecodeSecurityDefinitionForSymbolRequest();
			var requestId = securityDefinitionRequest.RequestId;
			L.LogInformation("SecurityDefinitionForSymbolRequest: {securityDefinitionRequest}", securityDefinitionRequest);
			var symbol = await _symbolsStore.GetSymbolAsync(securityDefinitionRequest.Symbol, ct);
			if (symbol == null)
			{
				L.LogInformation("Answer: SecurityDefinitionReject: NoSymbol");
				encoder.EncodeSecurityDefinitionReject(requestId, $"Symbol is unknown: {securityDefinitionRequest.Symbol}.");
				//// encoder.EncodeNoSecurityDefinitionsFound(requestId);
			}
			else
			{
				L.LogInformation("Answer: SecurityDefinitionResponse");
				encoder.EncodeSecurityDefinitionResponse(new EncodeSecurityDefinitionResponseArgs(requestId, symbol, true));
			}
			SendAsync(encoder.GetEncodedMessage());
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

		async Task ProcessMarketDataRequestAsync(IMessageDecoder decoder, IMessageEncoder encoder, CancellationToken ct)
		{
			var marketDataRequest = decoder.DecodeMarketDataRequest();
			L.LogInformation("MarketDataRequest: {marketDataRequest}", marketDataRequest);
			if (marketDataRequest.RequestAction != RequestActionEnum.Subscribe)
			{
				throw new NotSupportedException($"MarketDataRequestAction '{marketDataRequest.RequestAction}' is not supported.");
			}
			var symbol = await _symbolsStore.GetSymbolAsync(marketDataRequest.Symbol, ct);
			if (symbol == null)
			{
				L.LogInformation("Answer: MarketDataReject: NoSymbol");
				encoder.EncodeMarketDataReject(marketDataRequest.SymbolId, $"Symbol is unknown: {marketDataRequest.Symbol}.");
			}
			else if (!symbol.IsRealTime)
			{
				L.LogInformation("Answer: MarketDataReject: NoRealTimeSupport");
				encoder.EncodeMarketDataReject(marketDataRequest.SymbolId, $"Real-time market data is not supported for {marketDataRequest.Symbol}.");
			}
			else
			{
				L.LogInformation("Answer: MarketDataSnapshot");
				encoder.EncodeMarketDataSnapshot(marketDataRequest.SymbolId, TradingStatusEnum.TradingStatusUnknown, DateTime.Now);
			}
			SendAsync(encoder.GetEncodedMessage());
		}

		async Task ProcessHistoricalPriceDataRequestAsync(IMessageDecoder decoder, IMessageEncoder encoder, CancellationToken ct)
		{
			var historicalPriceDataRequest = decoder.DecodeHistoricalPriceDataRequest();
			var requestId = historicalPriceDataRequest.RequestId;
			L.LogInformation("RequestedHistory: {historicalPriceDataRequest}", historicalPriceDataRequest);
			var symbol = await _symbolsStore.GetSymbolAsync(historicalPriceDataRequest.Symbol, ct);
			if (symbol == null)
			{
				L.LogInformation("Answer: HistoricalPriceDataReject: NoSymbol");
				encoder.EncodeHistoricalPriceDataReject(historicalPriceDataRequest.RequestId, HistoricalPriceDataRejectReasonCodeEnum.HpdrGeneralRejectError, "No symbol found.");
			}
			else
			{
				var historyInterval = historicalPriceDataRequest.RecordInterval switch
				{
					HistoricalDataIntervalEnum.IntervalTick => HistoryInterval.Tick,
					< HistoricalDataIntervalEnum.Interval1Day => HistoryInterval.Intraday,
					_ => HistoryInterval.Daily
				};
				var store = await _symbolHistoryStoreProvider.GetSymbolHistoryStoreAsync(symbol, historyInterval, ct);
				var headerSent = false;
				var hasMore = false;
				using var reader = await store.CreateSymbolHistoryReaderAsync(
					symbol,
					historyInterval,
					historicalPriceDataRequest.StartDateTime,
					historicalPriceDataRequest.EndDateTime,
					HistoryDownloadBatchSize,
					ct);
				do
				{
					await L.LogOperationAsync(async () =>
					{
						// ReSharper disable once AccessToDisposedClosure
						var r = await reader.ReadSymbolHistoryAsync(ct);
						hasMore = r.HasMore;
						L.LogDebug("HasMore: {hasMore}", hasMore);
						if (!headerSent)
						{
							var noRecordsToReturn = r.Records.Count == 0;
							L.LogInformation("SendHeader({recordsExist})", !noRecordsToReturn);
							// TODO: ZLibCompression if client requests.
							encoder.EncodeHistoricalPriceDataResponseHeader(requestId, historicalPriceDataRequest.RecordInterval, false, noRecordsToReturn, 1);
							SendAsync(encoder.GetEncodedMessage());
						}
						var historyRecordsEncoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
						try
						{
							var recIndex = 0;
							var recsCount = r.Records.Count;
							L.LogInformation("EncodeAndSendRecords({recordsCount})", recsCount);
							foreach (var rec in r.Records)
							{
								historyRecordsEncoder.EncodeHistoricalPriceDataRecordResponse(
									requestId,
									rec.TimeStamp,
									rec.OpenPrice,
									rec.HighPrice,
									rec.LowPrice,
									rec.LastPrice,
									rec.Volume,
									0, 0, 0, !hasMore && recIndex == recsCount - 1);
								recIndex++;
							}
							SendAsync(historyRecordsEncoder.GetEncodedMessage());
						}
						finally
						{
							await historyRecordsEncoder.TryDisposeAsync();
						}
					}, "DownloadAndSendHistoryDataBatch");
				}
				// ReSharper disable once LoopVariableIsNeverChangedInsideLoop
				while (hasMore);
			}
		}

		void ProcessLogonRequest(IMessageDecoder decoder, IMessageEncoder encoder)
		{
			var logonRequest = decoder.DecodeLogonRequest();
			L.LogInformation("LogonInfo: {heartbeatIntervalInSeconds}, {clientName}, {hardwareIdentifier}", logonRequest.HeartbeatIntervalInSeconds, logonRequest.ClientName, logonRequest.HardwareIdentifier);
			StartHeartbeatTimer(logonRequest.HeartbeatIntervalInSeconds * 1000);
			L.LogInformation("Answer: LogonSuccess");
			encoder.EncodeLogonResponse(LogonStatusEnum.LogonSuccess, "Logon is successful.", _onlyHistoryServer);
			SendAsync(encoder.GetEncodedMessage());
		}

		void ProcessEncodingRequest(IMessageDecoder decoder, IMessageEncoder encoder)
		{
			if (!(decoder is DtcProtocol.Binary.MessageDecoder binaryDecoder))
			{
				throw new InvalidOperationException("Encoding request must be sent using binary protocol.");
			}
			var encodingRequest = binaryDecoder.DecodeEncodingRequest();
			if (encodingRequest.ProtocolVersion != MessageProtocol.Version)
			{
				throw new NotSupportedException($"Protocol version {encodingRequest.ProtocolVersion.ToInvStr()} is not supported. Supported: {MessageProtocol.Version}.");
			}
			switch (encodingRequest.Encoding)
			{
				default:
					if (_currentMessageProtocol.Encoding != MessageProtocol.PreferredEncoding)
					{
						_currentMessageProtocol.MessageStreamer.MessageBytesReceived -= OnMessageReceived;
						_currentMessageProtocol.Dispose();
						_currentMessageProtocol = MessageProtocol.CreateMessageProtocol(MessageProtocol.PreferredEncoding);
						_currentMessageProtocol.MessageStreamer.MessageBytesReceived += OnMessageReceived;
					}
					break;
			}
			L.LogInformation("ChosenEncoding: {chosenEncoding}", _currentMessageProtocol.Encoding);
			encoder.EncodeEncodingResponse(_currentMessageProtocol.Encoding);
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