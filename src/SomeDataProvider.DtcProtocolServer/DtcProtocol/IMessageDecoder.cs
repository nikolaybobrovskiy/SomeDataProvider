// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using System;

	using Dtc.Protocol.Private;

	// https://www.sierrachart.com/index.php?page=doc/DTCMessageDocumentation.php

	interface IMessageDecoder
	{
		/// <summary>The message's wire Type, one of the message type constants of <see cref="Dtc.Protocol.DtcConstants"/>.</summary>
		ushort DecodeMessageType();

		LogonRequest DecodeLogonRequest();

		HistoricalPriceDataRequest DecodeHistoricalPriceDataRequest();

		MarketDataRequest DecodeMarketDataRequest();

		SecurityDefinitionForSymbolRequest DecodeSecurityDefinitionForSymbolRequest();

		SymbolSearchRequest DecodeSymbolSearchRequest();

		/// <summary>Decodes <c>SERIES_EXTENSION_REQUEST</c>, the handshake of the private series extension.</summary>
		/// <exception cref="NotSupportedException">The encoding is not binary with variable-length strings, the only one of the extension.</exception>
		SeriesExtensionRequest DecodeSeriesExtensionRequest();

		/// <summary>Decodes <c>SERIES_METADATA_REQUEST</c> of the private series extension, with its strings as UTF-8.</summary>
		/// <exception cref="InvalidSeriesRequestException">The message is malformed, but its RequestID can be read.</exception>
		/// <exception cref="System.IO.InvalidDataException">The message is malformed, and its RequestID cannot be read.</exception>
		/// <exception cref="NotSupportedException">The encoding is not binary with variable-length strings, the only one of the extension.</exception>
		SeriesMetadataRequest DecodeSeriesMetadataRequest();

		/// <summary>Decodes <c>SERIES_VINTAGES_REQUEST</c> of the private series extension, with its strings as UTF-8.</summary>
		/// <exception cref="InvalidSeriesRequestException">
		/// The message is malformed, but its RequestID can be read; or one of its dates is nonzero but not a date.
		/// </exception>
		/// <exception cref="System.IO.InvalidDataException">The message is malformed, and its RequestID cannot be read.</exception>
		/// <exception cref="NotSupportedException">The encoding is not binary with variable-length strings, the only one of the extension.</exception>
		SeriesVintagesRequest DecodeSeriesVintagesRequest();
	}

	interface IMessageDecoderFactory
	{
		IMessageDecoder CreateMessageDecoder(Memory<byte> buffer);
	}
}