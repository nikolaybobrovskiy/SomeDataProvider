// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using System;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	interface IMessageEncoder
	{
		void EncodeEncodingResponse(EncodingEnum encoding);

		void EncodeLogonResponse(LogonStatusEnum logonStatus, string resultText, bool oneHistoricalPriceDataRequestPerConnection);

		void EncodeHeartbeatMessage(uint numDroppedMessages);

		void EncodeHistoricalPriceDataReject(int requestId, HistoricalPriceDataRejectReasonCodeEnum rejectReasonCode, string rejectText);

		void EncodeMarketDataReject(uint symbolId, string rejectText);

		void EncodeMarketDataSnapshot(uint symbolId, TradingStatusEnum tradingStatus, DateTime lastTradeDateTime);

		void EncodeSecurityDefinitionReject(int requestId, string rejectText);

		void EncodeSecurityDefinitionResponse(int requestId, bool isFinalMessage, string? symbol, string? exchange, SecurityTypeEnum securityType, string? description, PriceDisplayFormatEnum priceDisplayFormat, string? currency, bool isDelayed);

		byte[] GetEncodedMessage();
	}

	interface IMessageEncoderFactory
	{
		IMessageEncoder CreateMessageEncoder();
	}

	static class MessageEncoderDefinitions
	{
		public static void EncodeNoSecurityDefinitionsFound(this IMessageEncoder encoder, int requestId)
		{
			encoder.EncodeSecurityDefinitionResponse(requestId, true, string.Empty, string.Empty, SecurityTypeEnum.SecurityTypeUnset, string.Empty, PriceDisplayFormatEnum.PriceDisplayFormatUnset, string.Empty, false);
		}
	}
}