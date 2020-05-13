// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	interface IMessageDecoder
	{
		MessageTypeEnum DecodeMessageType();

		LogonRequest DecodeLogonRequest();

		HistoricalPriceDataRequest DecodeHistoricalPriceDataRequest();
	}

	interface IMessageDecoderFactory
	{
		IMessageDecoder CreateMessageDecoder(byte[] buffer, int offset, int size);
	}
}