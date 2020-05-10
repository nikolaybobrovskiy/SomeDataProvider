namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;

	using NBLib.BuiltInTypes;

	class MessageEncoder : IMessageEncoder
	{
		protected byte[]? Bytes { get; set; }

		public void EncodeEncodingResponse(in EncodingResponse encodingResponse)
		{
			Bytes = StructConverter.StructToByteArray(encodingResponse);
		}

		public byte[] GetEncodedMessage()
		{
			return Bytes ?? throw new InvalidOperationException("Message is not ready.");
		}

		public sealed class Factory : IMessageEncoderFactory
		{
			public IMessageEncoder CreateMessageEncoder()
			{
				return new MessageEncoder();
			}
		}
	}
}