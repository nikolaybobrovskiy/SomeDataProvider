// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using System;

	using NBLib.CodeFlow;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	sealed class MessageProtocol : IDisposable
	{
		public const int Version = 8;

		public const EncodingEnum PreferredEncoding = EncodingEnum.BinaryWithVariableLengthStrings;

		MessageProtocol(EncodingEnum encoding, IMessageDecoderFactory messageDecoderFactory, IMessageEncoderFactory messageEncoderFactory, IMessageStreamer messageStreamer)
		{
			Encoding = encoding;
			MessageDecoderFactory = messageDecoderFactory;
			MessageEncoderFactory = messageEncoderFactory;
			MessageStreamer = messageStreamer;
		}

		public EncodingEnum Encoding { get; }

		public IMessageDecoderFactory MessageDecoderFactory { get; }

		public IMessageEncoderFactory MessageEncoderFactory { get; }

		public IMessageStreamer MessageStreamer { get; }

		public static MessageProtocol CreateMessageProtocol(EncodingEnum encoding)
		{
			IMessageDecoderFactory decoderFactory;
			IMessageEncoderFactory encoderFactory;
			IMessageStreamer messageStreamer;
			switch (encoding)
			{
				case EncodingEnum.BinaryEncoding:
					decoderFactory = new Binary.MessageDecoder.Factory();
					encoderFactory = new Binary.MessageEncoder.Factory();
					messageStreamer = new Binary.MessageStreamer();
					break;
				case EncodingEnum.BinaryWithVariableLengthStrings:
					decoderFactory = new BinaryVls.MessageDecoder.Factory();
					encoderFactory = new BinaryVls.MessageEncoder.Factory();
					messageStreamer = new Binary.MessageStreamer();
					break;
				default: throw new NotSupportedException($"Encoding '{encoding}' is not supported.");
			}
			return new MessageProtocol(encoding, decoderFactory, encoderFactory, messageStreamer);
		}

		public void Dispose()
		{
			MessageDecoderFactory.TryDispose();
			MessageEncoderFactory.TryDispose();
			MessageStreamer.TryDispose();
		}
	}
}