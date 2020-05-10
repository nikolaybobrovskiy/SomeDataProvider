namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;

	using NBLib.BuiltInTypes;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class MessageDecoder : IMessageDecoder
	{
		protected MessageDecoder(byte[] buffer, int offset, int size)
		{
			Buffer = buffer;
			Offset = offset;
			Size = size;
		}

		protected ReadOnlySpan<byte> BufferSpan => Buffer.AsSpan(Offset, Size);

		protected byte[] Buffer { get; }

		protected int Offset { get; }

		protected int Size { get; }

		public MessageTypeEnum DecodeMessageType()
		{
			return (MessageTypeEnum)BitConverter.ToUInt16(BufferSpan.Slice(2, 2));
		}

		public virtual DtcProtocol.LogonRequest DecodeLogonRequest()
		{
			var logonRequest = StructConverter.ByteArrayToStruct<LogonRequest>(Buffer, Offset);
			return new DtcProtocol.LogonRequest(logonRequest.HeartbeatIntervalInSeconds);
		}

		public EncodingRequest DecodeEncodingRequest()
		{
			return StructConverter.ByteArrayToStruct<EncodingRequest>(Buffer, Offset);
		}

		public sealed class Factory : IMessageDecoderFactory
		{
			public IMessageDecoder CreateMessageDecoder(byte[] buffer, int offset, int size)
			{
				return new MessageDecoder(buffer, offset, size);
			}
		}
	}
}