namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;
	using System.Runtime.InteropServices;

	using NBLib.BuiltInTypes;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class MessageEncoder : IMessageEncoder
	{
		protected byte[]? Bytes { get; set; }

		public void EncodeEncodingResponse(EncodingEnum encoding)
		{
			Bytes = StructConverter.StructToByteArray(new EncodingResponse
			{
				Size = Convert.ToUInt16(Marshal.SizeOf(typeof(EncodingResponse))),
				Type = MessageTypeEnum.EncodingResponse,
				ProtocolVersion = MessageProtocol.Version,
				Encoding = encoding,
			});
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