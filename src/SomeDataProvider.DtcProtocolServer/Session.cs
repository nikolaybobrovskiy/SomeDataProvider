// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer
{
	using System;
	using System.Timers;

	using Microsoft.Extensions.Logging;

	using NBLib.BuiltInTypes;
	using NBLib.CodeFlow;
	using NBLib.Logging;

	using NetCoreServer;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class Session : TcpSession
	{
		readonly bool _onlyHistoryServer;
		readonly object _singleWorkerLock = new object();
		MessageProtocol _currentMessageProtocol = MessageProtocol.CreateMessageProtocol(EncodingEnum.BinaryEncoding);
		Timer? _timer;

		public Session(TcpServer server, bool onlyHistoryServer, ILoggerFactory loggerFactory)
			: base(server)
		{
			_onlyHistoryServer = onlyHistoryServer;
			L = loggerFactory.CreateLogger<Session>();
		}

		ILogger<Session> L { get; }

		protected override void Dispose(bool disposingManagedResources)
		{
			if (!disposingManagedResources) return;
			StopHeartbeatTimer();
		}

		protected override void OnDisconnected()
		{
			StopHeartbeatTimer();
		}

		protected override void OnReceived(byte[] buffer, long offset, long size)
		{
			try
			{
				L.LogOperation(() =>
				{
					L.LogDebug("WaitingForLock");
					lock (_singleWorkerLock)
					{
						L.LogDebug("LockAcquired");
						var decoder = _currentMessageProtocol.MessageDecoderFactory.CreateMessageDecoder(buffer, Convert.ToInt32(offset), Convert.ToInt32(size));
						var encoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
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
			catch (Exception ex) when (!(ex is OperationCanceledException))
			{
				L.LogError(ex, "Error while processing request.");
			}
		}

		void ProcessSecurityDefinitionForSymbolRequest(IMessageDecoder decoder, IMessageEncoder encoder)
		{
			throw new NotImplementedException();
		}

		void ProcessMarketDataRequest(IMessageDecoder decoder, IMessageEncoder encoder)
		{
			var marketDataRequest = decoder.DecodeMarketDataRequest();
			L.LogInformation("MarketDataRequest: {marketDataRequest}", marketDataRequest);
			//encoder.EncodeMarketDataReject(marketDataRequest.SymbolId, $"Symbol is unknown: {marketDataRequest.Symbol}.");
			L.LogInformation("Answer: MarketDataSnapshot");
			encoder.EncodeMarketDataSnapshot(marketDataRequest.SymbolId, TradingStatusEnum.TradingStatusUnknown, DateTime.Now);
			Send(encoder.GetEncodedMessage());
		}

		void ProcessHistoricalPriceDataRequest(IMessageDecoder decoder, IMessageEncoder encoder)
		{
			var historicalPriceDataRequest = decoder.DecodeHistoricalPriceDataRequest();
			L.LogInformation("RequestedHistory: {requestId}, {exchange}, {symbol}, {recordInterval}", historicalPriceDataRequest.RequestId, historicalPriceDataRequest.Exchange, historicalPriceDataRequest.Symbol, historicalPriceDataRequest.RecordInterval);
			L.LogInformation("Answer: HistoricalPriceDataReject");
			encoder.EncodeHistoricalPriceDataReject(historicalPriceDataRequest.RequestId, HistoricalPriceDataRejectReasonCodeEnum.HpdrGeneralRejectError, "No symbol found.");
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
				//// 		_currentMessageProtocol = MessageProtocol.CreateMessageProtocol(EncodingEnum.BinaryEncoding);
				//// 	break;
				default:
					if (_currentMessageProtocol.Encoding != MessageProtocol.PreferredEncoding)
						_currentMessageProtocol = MessageProtocol.CreateMessageProtocol(MessageProtocol.PreferredEncoding);
					break;
			}
			L.LogInformation("ChosenEncoding: {chosenEncoding}", _currentMessageProtocol.Encoding);
			encoder.EncodeEncodingResponse(_currentMessageProtocol.Encoding);
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