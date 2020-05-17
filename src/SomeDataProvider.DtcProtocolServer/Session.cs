// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer
{
	using System;
	using System.Context;
	using System.Threading;
	using System.Timers;

	using Microsoft.Extensions.Logging;

	using NBLib.BuiltInTypes;
	using NBLib.CodeFlow;
	using NBLib.Logging;

	using NetCoreServer;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	using Timer = System.Timers.Timer;

	class Session : TcpSession
	{
		readonly bool _onlyHistoryServer;
		readonly ISymbolsStore _symbolsStore;
		readonly object _singleWorkerLock = new object();
#pragma warning disable CC0033 // Dispose Fields Properly: disposed in Dispose() override.
		readonly CancellationTokenSource _cts = new CancellationTokenSource();
#pragma warning restore CC0033 // Dispose Fields Properly
		MessageProtocol _currentMessageProtocol = MessageProtocol.CreateMessageProtocol(EncodingEnum.BinaryEncoding);
		Timer? _timer;

		public Session(TcpServer server, bool onlyHistoryServer, ISymbolsStore symbolsStore, ILoggerFactory loggerFactory)
			: base(server)
		{
			_currentMessageProtocol.MessageStreamer.MessageBytesReceived += OnMessageReceived;
			_onlyHistoryServer = onlyHistoryServer;
			_symbolsStore = symbolsStore;
			L = loggerFactory.CreateLogger<Session>();
		}

		ILogger<Session> L { get; }

		protected override void Dispose(bool disposingManagedResources)
		{
			if (!disposingManagedResources) return;
			StopHeartbeatTimer();
			lock (_singleWorkerLock)
			{
				_currentMessageProtocol.MessageStreamer.MessageBytesReceived -= OnMessageReceived;
				_currentMessageProtocol.Dispose();
			}
			_cts.Dispose();
		}

		protected override void OnSent(long sent, long pending)
		{
			L.LogDebug("Sent: {sent}/{pending}", sent, pending);
		}

		protected override void OnDisconnected()
		{
			StopHeartbeatTimer();
			_cts.Cancel();
		}

		protected override void OnReceived(byte[] buffer, long offset, long size)
		{
			L.LogDebug("BytesReceived: {bytesReceived}", size);
			lock (_singleWorkerLock)
			{
				_currentMessageProtocol.MessageStreamer.PutReceivedBytes(buffer.AsMemory(Convert.ToInt32(offset), Convert.ToInt32(size)));
			}
		}

		void OnMessageReceived(Memory<byte> buffer)
		{
			try
			{
				L.LogOperation(() =>
				{
					L.LogDebug("WaitingForLock");
					L.LogDebug("LockAcquired");
					using (RunContext.WithCancel(_cts.Token, "Disconnected.", CancellationReason.OperationAborted))
					{
						// ReSharper disable InconsistentlySynchronizedField
						var decoder = _currentMessageProtocol.MessageDecoderFactory.CreateMessageDecoder(buffer);
						var encoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
						// ReSharper restore InconsistentlySynchronizedField
						using (new Finally(() => decoder.TryDispose()))
						using (new Finally(() => encoder.TryDispose()))
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
									break;
								case MessageTypeEnum.Heartbeat:
									// TODO: Add Heartbeat detection logic.
									// It is recommended that if there is a loss of HEARTBEAT messages from the other side, for twice the amount of the HeartbeatIntervalInSeconds time that it is safe to assume that the other side is no longer present and the network socket should be then gracefully closed.
									break;
								case MessageTypeEnum.HistoricalPriceDataRequest:
									ProcessHistoricalPriceDataRequest(decoder, encoder);
									break;
								case MessageTypeEnum.MarketDataRequest:
									ProcessMarketDataRequest(decoder, encoder);
									break;
								case MessageTypeEnum.SecurityDefinitionForSymbolRequest:
									ProcessSecurityDefinitionForSymbolRequest(decoder, encoder);
									break;
								default:
									throw new NotSupportedException($"Message type is not supported: {messageType}.");
							}
						}
					}
				}, "ProcessRequest");
			}
			catch (OperationCanceledException ex)
			{
				L.LogWarning(ex, $"Request execution was interrupted: {GetContext.GetCancellationContext(ex.CancellationToken).ReasonDescription.IfEmpty("Unknown cancellation.")}");
			}
			catch (Exception ex)
			{
				L.LogError(ex, "Error while processing request.");
			}
		}

		void ProcessSecurityDefinitionForSymbolRequest(IMessageDecoder decoder, IMessageEncoder encoder)
		{
			var ct = GetContext.CancellationToken;
			var securityDefinitionRequest = decoder.DecodeSecurityDefinitionForSymbolRequest();
			var requestId = securityDefinitionRequest.RequestId;
			L.LogInformation("SecurityDefinitionForSymbolRequest: {securityDefinitionRequest}", securityDefinitionRequest);
			var symbol = _symbolsStore.GetSymbolAsync(securityDefinitionRequest.Symbol, ct).GetAwaiter().GetResult();
			if (symbol == null)
			{
				L.LogInformation("Answer: SecurityDefinitionReject: NoSymbol");
				encoder.EncodeSecurityDefinitionReject(requestId, $"Symbol is unknown: {securityDefinitionRequest.Symbol}.");
				////encoder.EncodeNoSecurityDefinitionsFound(requestId);
			}
			else
			{
				L.LogInformation("Answer: SecurityDefinitionResponse");
				encoder.EncodeSecurityDefinitionResponse(
					requestId,
					true,
					symbol.Code,
					symbol.Exchange,
					(SecurityTypeEnum)symbol.Type,
					symbol.Description,
					(PriceDisplayFormatEnum)symbol.NumberOfDecimals,
					symbol.Currency,
					symbol.IsDelayed);
			}
			Send(encoder.GetEncodedMessage());
		}

		void ProcessMarketDataRequest(IMessageDecoder decoder, IMessageEncoder encoder)
		{
			var ct = GetContext.CancellationToken;
			var marketDataRequest = decoder.DecodeMarketDataRequest();
			L.LogInformation("MarketDataRequest: {marketDataRequest}", marketDataRequest);
			if (marketDataRequest.RequestAction != RequestActionEnum.Subscribe)
			{
				throw new NotSupportedException($"MarketDataRequestAction '{marketDataRequest.RequestAction}' is not supported.");
			}
			var symbol = _symbolsStore.GetSymbolAsync(marketDataRequest.Symbol, ct).GetAwaiter().GetResult();
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
			Send(encoder.GetEncodedMessage());
		}

		void ProcessHistoricalPriceDataRequest(IMessageDecoder decoder, IMessageEncoder encoder)
		{
			var ct = GetContext.CancellationToken;
			var historicalPriceDataRequest = decoder.DecodeHistoricalPriceDataRequest();
			var requestId = historicalPriceDataRequest.RequestId;
			L.LogInformation("RequestedHistory: {historicalPriceDataRequest}", historicalPriceDataRequest);
			var symbol = _symbolsStore.GetSymbolAsync(historicalPriceDataRequest.Symbol, ct).GetAwaiter().GetResult();
			if (symbol == null)
			{
				L.LogInformation("Answer: HistoricalPriceDataReject: NoSymbol");
				encoder.EncodeHistoricalPriceDataReject(historicalPriceDataRequest.RequestId, HistoricalPriceDataRejectReasonCodeEnum.HpdrGeneralRejectError, "No symbol found.");
			}
			else
			{
				var noRecordsToReturn = true;
				// Get history
				encoder.EncodeHistoricalPriceDataResponseHeader(requestId, historicalPriceDataRequest.RecordInterval, false, noRecordsToReturn, 1);
			}
			Send(encoder.GetEncodedMessage());
		}

		void ProcessLogonRequest(IMessageDecoder decoder, IMessageEncoder encoder)
		{
			var logonRequest = decoder.DecodeLogonRequest();
			L.LogInformation("LogonInfo: {heartbeatIntervalInSeconds}, {clientName}, {hardwareIdentifier}", logonRequest.HeartbeatIntervalInSeconds, logonRequest.ClientName, logonRequest.HardwareIdentifier);
			StartHeartbeatTimer(logonRequest.HeartbeatIntervalInSeconds * 1000);
			L.LogInformation("Answer: LogonSuccess");
			encoder.EncodeLogonResponse(LogonStatusEnum.LogonSuccess, "Logon is successful.", _onlyHistoryServer);
			Send(encoder.GetEncodedMessage());
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
				//// case EncodingEnum.BinaryEncoding:
				//// 	if (_currentMessageProtocol.Encoding != EncodingEnum.BinaryEncoding)
				//// 	{
				////        _currentMessageProtocol.MessageStreamer.MessageBytesReceived -= OnMessageReceived;
				//// 		_currentMessageProtocol.Dispose();
				//// 		_currentMessageProtocol = MessageProtocol.CreateMessageProtocol(EncodingEnum.BinaryEncoding);
				////        _currentMessageProtocol.MessageStreamer.MessageBytesReceived += OnMessageReceived;
				//// 	}
				//// 	break;
				default:
					// ReSharper disable InconsistentlySynchronizedField
					if (_currentMessageProtocol.Encoding != MessageProtocol.PreferredEncoding)
					{
						_currentMessageProtocol.MessageStreamer.MessageBytesReceived -= OnMessageReceived;
						_currentMessageProtocol.Dispose();
						_currentMessageProtocol = MessageProtocol.CreateMessageProtocol(MessageProtocol.PreferredEncoding);
						_currentMessageProtocol.MessageStreamer.MessageBytesReceived += OnMessageReceived;
					}
					// ReSharper restore InconsistentlySynchronizedField
					break;
			}
			// ReSharper disable InconsistentlySynchronizedField
			L.LogInformation("ChosenEncoding: {chosenEncoding}", _currentMessageProtocol.Encoding);
			encoder.EncodeEncodingResponse(_currentMessageProtocol.Encoding);
			// ReSharper restore InconsistentlySynchronizedField
			Send(encoder.GetEncodedMessage());
		}

		void OnHeartbeatTimerElapsed(object sender, ElapsedEventArgs e)
		{
			L.LogOperation(() =>
			{
				if (IsDisposed || !IsConnected)
				{
					L.LogDebug("DisposedOrDisconnected");
					return;
				}
				L.LogDebug("WaitingForLock");
				lock (_singleWorkerLock)
				{
					L.LogDebug("LockAcquired");
					var encoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
					using (new Finally(() => encoder.TryDispose()))
					{
						encoder.EncodeHeartbeatMessage(0);
						L.LogInformation("Sending hearbeat...");
						Send(encoder.GetEncodedMessage());
						L.LogInformation("Sent hearbeat.");
					}
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
			_timer = t;
		}

		void StopHeartbeatTimer()
		{
			if (_onlyHistoryServer) return;
			var t = _timer;
			t?.Stop();
			L.LogInformation("HearbeatTimerStopped");
			t?.Dispose();
		}
	}
}