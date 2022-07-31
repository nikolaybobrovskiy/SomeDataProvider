// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System;

using Microsoft.Extensions.Logging;

using NBLib.BuiltInTypes;

using SomeDataProvider.DtcProtocolServer.DtcProtocol;

partial class Session
{
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
}