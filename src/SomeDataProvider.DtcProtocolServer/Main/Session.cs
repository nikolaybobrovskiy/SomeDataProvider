// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Main
{
	using System;

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
		MessageProtocol _currentMessageProtocol = MessageProtocol.CreateMessageProtocol(EncodingEnum.BinaryEncoding);

		public Session(TcpServer server, ILoggerFactory loggerFactory)
			: base(server)
		{
			L = loggerFactory.CreateLogger<Session>();
		}

		ILogger<Session> L { get; }

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
								decoder.DecodeLogonRequest();
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