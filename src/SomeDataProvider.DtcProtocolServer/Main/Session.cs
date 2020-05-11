// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Main
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

	using Finally = NBLib.CodeFlow.Finally;

	public class Session : TcpSession
	{
		// volatile - consumed by timer and can be changed by request thread.
		volatile MessageProtocol _currentMessageProtocol = MessageProtocol.CreateMessageProtocol(EncodingEnum.BinaryEncoding);
		Timer? _timer;

		public Session(TcpServer server, ILoggerFactory loggerFactory)
			: base(server)
		{
			L = loggerFactory.CreateLogger<Session>();
		}

		ILogger<Session> L { get; }

		protected override void Dispose(bool disposingManagedResources)
		{
			if (!disposingManagedResources) return;
			_timer?.Dispose();
		}

		protected override void OnReceived(byte[] buffer, long offset, long size)
		{
			try
			{
				L.LogOperation(() =>
				{
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
							default:
								throw new NotSupportedException($"Message type is not supported: {messageType}.");
						}
					}
				}, "ProcessRequest");
			}
			catch (Exception ex) when (!(ex is OperationCanceledException))
			{
				L.LogError(ex, "Error while processing request.");
			}
		}

		void ProcessLogonRequest(IMessageDecoder decoder, IMessageEncoder encoder)
		{
			var logonRequest = decoder.DecodeLogonRequest();
			L.LogInformation("LogonInfo: {heartbeatIntervalInSeconds}, {clientName}, {hardwareIdentifier}", logonRequest.HeartbeatIntervalInSeconds, logonRequest.ClientName, logonRequest.HardwareIdentifier);
			_timer?.Dispose();
			_timer = new Timer(logonRequest.HeartbeatIntervalInSeconds * 20000);
			_timer.Elapsed += OnHeartbeatTimerElapsed;
			_timer.Start();
			// TODO: Save logonRequest.HeartbeatIntervalInSeconds and initiate heartbeat.
			encoder.EncodeLogonResponse(LogonStatusEnum.LogonSuccess, "Logon is successful.");
			Send(encoder.GetEncodedMessage());
		}

		void OnHeartbeatTimerElapsed(object sender, ElapsedEventArgs e)
		{
			var encoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
			using (new Finally(() => encoder.TryDispose()))
			{
				encoder.EncodeHeartbeatMessage(0);
				L.LogInformation("Sending hearbeat...");
				Send(encoder.GetEncodedMessage());
				L.LogInformation("Sent hearbeat.");
			}
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
				case EncodingEnum.BinaryEncoding:
					if (_currentMessageProtocol.Encoding != EncodingEnum.BinaryEncoding)
						_currentMessageProtocol = MessageProtocol.CreateMessageProtocol(EncodingEnum.BinaryEncoding);
					break;
				default:
					if (_currentMessageProtocol.Encoding != MessageProtocol.PreferredEncoding)
						_currentMessageProtocol = MessageProtocol.CreateMessageProtocol(MessageProtocol.PreferredEncoding);
					break;
			}
			encoder.EncodeEncodingResponse(_currentMessageProtocol.Encoding);
			Send(encoder.GetEncodedMessage());
		}
	}
}