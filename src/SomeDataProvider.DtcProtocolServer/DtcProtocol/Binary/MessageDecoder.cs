namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;

	using NBLib.BuiltInTypes;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class MessageDecoder : IMessageDecoder
	{
		protected MessageDecoder(byte[] buffer, in long offset, in long size)
		{
			Buffer = buffer;
			Offset = offset;
			Size = size;
		}

		protected byte[] Buffer { get; }

		protected long Offset { get; }

		protected long Size { get; }

		public MessageTypeEnum DecodeMessageType()
		{
			return (MessageTypeEnum)BitConverter.ToUInt16(Buffer.AsSpan(2, 2));
		}

		public EncodingRequest DecodeEncodingRequest()
		{
			return StructConverter.ByteArrayToStruct<EncodingRequest>(Buffer);
		}

		public sealed class Factory : IMessageDecoderFactory
		{
			public IMessageDecoder CreateMessageDecoder(byte[] buffer, long offset, long size)
			{
				return new MessageDecoder(buffer, offset, size);
			}
		}
	}
}