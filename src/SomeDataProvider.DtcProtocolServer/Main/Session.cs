namespace SomeDataProvider.DtcProtocolServer.Main
{
	using System;
	using System.Runtime.InteropServices;

	using Microsoft.Extensions.Logging;

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
					var decoder = _currentMessageProtocol.MessageDecoderFactory.CreateMessageDecoder(buffer, offset, size);
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
			var encodingRequest = decoder.DecodeEncodingRequest();
			if (encodingRequest.ProtocolVersion != MessageProtocol.Version)
			{
				throw new NotSupportedException($"Protocol version {encodingRequest.ProtocolVersion} is not supported. Supported: {MessageProtocol.Version}.");
			}
			MessageProtocol? newVersionProtocol = null;
			switch (encodingRequest.Encoding)
			{
				case EncodingEnum.BinaryEncoding:
					if (_currentMessageProtocol.Encoding != EncodingEnum.BinaryEncoding)
						newVersionProtocol = MessageProtocol.CreateMessageProtocol(EncodingEnum.BinaryEncoding);
					break;
				default:
					if (_currentMessageProtocol.Encoding != EncodingEnum.BinaryWithVariableLengthStrings)
						newVersionProtocol = MessageProtocol.CreateMessageProtocol(EncodingEnum.BinaryWithVariableLengthStrings);
					break;
			}
			encoder.EncodeEncodingResponse(new EncodingResponse
			{
				Size = Convert.ToUInt16(Marshal.SizeOf(typeof(EncodingResponse))),
				Type = MessageTypeEnum.EncodingResponse,
				ProtocolVersion = encodingRequest.ProtocolVersion,
				Encoding = _currentMessageProtocol.Encoding,
			});
			Send(encoder.GetEncodedMessage());
			if (newVersionProtocol != null)
			{
				_currentMessageProtocol = newVersionProtocol;
			}
		}
	}
}