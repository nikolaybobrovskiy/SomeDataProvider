namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	interface IMessageDecoder
	{
		MessageTypeEnum DecodeMessageType();

		LogonRequest DecodeLogonRequest();
	}

	interface IMessageDecoderFactory
	{
		IMessageDecoder CreateMessageDecoder(byte[] buffer, int offset, int size);
	}
}