// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using System;

	// https://www.sierrachart.com/index.php?page=doc/DTCMessageDocumentation.php

	interface IMessageDecoder
	{
		/// <summary>The message's wire Type, one of the message type constants of <see cref="Dtc.Protocol.DtcConstants"/>.</summary>
		ushort DecodeMessageType();

		LogonRequest DecodeLogonRequest();

		HistoricalPriceDataRequest DecodeHistoricalPriceDataRequest();

		MarketDataRequest DecodeMarketDataRequest();

		SecurityDefinitionForSymbolRequest DecodeSecurityDefinitionForSymbolRequest();
	}

	interface IMessageDecoderFactory
	{
		IMessageDecoder CreateMessageDecoder(Memory<byte> buffer);
	}
}