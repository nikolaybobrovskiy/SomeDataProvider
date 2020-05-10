namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using System;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	sealed class MessageProtocol
	{
		public const int Version = 8;

		public const EncodingEnum PreferredEncoding = EncodingEnum.BinaryWithVariableLengthStrings;

		MessageProtocol(EncodingEnum encoding, IMessageDecoderFactory messageDecoderFactory, IMessageEncoderFactory messageEncoderFactory)
		{
			Encoding = encoding;
			MessageDecoderFactory = messageDecoderFactory;
			MessageEncoderFactory = messageEncoderFactory;
		}

		public EncodingEnum Encoding { get; }

		public IMessageDecoderFactory MessageDecoderFactory { get; }

		public IMessageEncoderFactory MessageEncoderFactory { get; }

		public static MessageProtocol CreateMessageProtocol(EncodingEnum encoding)
		{
			IMessageDecoderFactory decoderFactory;
			IMessageEncoderFactory encoderFactory;
			switch (encoding)
			{
				case EncodingEnum.BinaryEncoding:
					decoderFactory = new Binary.MessageDecoder.Factory();
					encoderFactory = new Binary.MessageEncoder.Factory();
					break;
				case EncodingEnum.BinaryWithVariableLengthStrings:
					decoderFactory = new BinaryVls.MessageDecoder.Factory();
					encoderFactory = new BinaryVls.MessageEncoder.Factory();
					break;
				default: throw new NotSupportedException($"Encoding '{encoding}' is not supported.");
			}
			return new MessageProtocol(encoding, decoderFactory, encoderFactory);
		}
	}
}