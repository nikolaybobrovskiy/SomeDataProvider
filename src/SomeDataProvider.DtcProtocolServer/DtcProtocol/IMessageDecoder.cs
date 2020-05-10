namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	interface IMessageDecoder
	{
		MessageTypeEnum DecodeMessageType();

		EncodingRequest DecodeEncodingRequest();
	}

	interface IMessageDecoderFactory
	{
		IMessageDecoder CreateMessageDecoder(byte[] buffer, long offset, long size);
	}
}