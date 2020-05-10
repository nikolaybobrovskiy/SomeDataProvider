namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	interface IMessageDecoder
	{
		MessageTypeEnum DecodeMessageType();
	}

	interface IMessageDecoderFactory
	{
		IMessageDecoder CreateMessageDecoder(byte[] buffer, long offset, long size);
	}
}