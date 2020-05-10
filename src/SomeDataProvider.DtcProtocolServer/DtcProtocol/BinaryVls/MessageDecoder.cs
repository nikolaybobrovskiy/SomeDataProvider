namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;

	using NBLib.BuiltInTypes;

	class MessageDecoder : Binary.MessageDecoder
	{
		MessageDecoder(byte[] buffer, int offset, int size)
			: base(buffer, offset, size)
		{
		}

		public override DtcProtocol.LogonRequest DecodeLogonRequest()
		{
			var logonRequest = StructConverter.ByteArrayToStruct<LogonRequest>(Buffer, Offset);
			return new DtcProtocol.LogonRequest(logonRequest.HeartbeatIntervalInSeconds);
		}

		public new sealed class Factory : IMessageDecoderFactory
		{
			public IMessageDecoder CreateMessageDecoder(byte[] buffer, int offset, int size)
			{
				return new MessageDecoder(buffer, offset, size);
			}
		}
	}
}