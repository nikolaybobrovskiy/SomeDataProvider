// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using System;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	// https://www.sierrachart.com/index.php?page=doc/DTCMessageDocumentation.php

	interface IMessageEncoder
	{
		int EncodedMessageSize { get; }

		void EncodeEncodingResponse(EncodingEnum encoding);

		void EncodeLogonResponse(LogonStatusEnum logonStatus, string resultText, bool oneHistoricalPriceDataRequestPerConnection);

		void EncodeHeartbeatMessage(uint numDroppedMessages);

		void EncodeHistoricalPriceDataReject(int requestId, HistoricalPriceDataRejectReasonCodeEnum rejectReasonCode, string rejectText);

		void EncodeMarketDataReject(uint symbolId, string rejectText);

		void EncodeMarketDataSnapshot(uint symbolId, TradingStatusEnum tradingStatus, DateTime lastTradeDateTime);

		void EncodeSecurityDefinitionReject(int requestId, string rejectText);

		void EncodeSecurityDefinitionResponse(EncodeSecurityDefinitionResponseArgs args);

		void EncodeHistoricalPriceDataResponseHeader(int requestId, HistoricalDataIntervalEnum recordInterval, bool useZLibCompression, bool noRecordsToReturn, float intToFloatPriceDivisor);

		void EncodeHistoricalPriceDataRecordResponse(int requestId, DateTime startDateTime, double openPrice, double highPrice, double lowPrice, double lastPrice, double volume, uint openInterestOrNumTrades, double bidVolume, double askVolume, bool isFinalRecord);

		byte[] GetEncodedMessage();

		readonly ref struct EncodeSecurityDefinitionResponseArgs
		{
			public EncodeSecurityDefinitionResponseArgs(int requestId, ISymbol symbol, bool isFinalMessage = false)
			{
				RequestId = requestId;
				IsFinalMessage = isFinalMessage;
				Symbol = symbol.Code;
				Exchange = symbol.Exchange;
				SecurityType = (SecurityTypeEnum)symbol.Type;
				Description = symbol.Description;
				PriceDisplayFormat = (PriceDisplayFormatEnum)symbol.NumberOfDecimals;
				Currency = symbol.Currency;
				IsDelayed = symbol.IsDelayed;
				MinPriceIncrement = symbol.MinPriceIncrement;
			}

			public int RequestId { get; init; }
			public bool IsFinalMessage { get; init; }
			public string? Symbol { get; init; }
			public string? Exchange { get; init; }
			public SecurityTypeEnum SecurityType { get; init; }
			public string? Description { get; init; }
			public PriceDisplayFormatEnum PriceDisplayFormat { get; init; }
			public float MinPriceIncrement { get; init; }
			public string? Currency { get; init; }
			public bool IsDelayed { get; init; }
		}
	}

	interface IMessageEncoderFactory
	{
		IMessageEncoder CreateMessageEncoder();
	}

	static class MessageEncoderExtensions
	{
		public static void EncodeNoSecurityDefinitionsFound(this IMessageEncoder encoder, int requestId)
		{
			encoder.EncodeSecurityDefinitionResponse(new IMessageEncoder.EncodeSecurityDefinitionResponseArgs
			{
				RequestId = requestId,
				IsFinalMessage = true,
				Symbol = string.Empty,
				Exchange = string.Empty,
				SecurityType = SecurityTypeEnum.SecurityTypeUnset,
				Description = string.Empty,
				PriceDisplayFormat = PriceDisplayFormatEnum.PriceDisplayFormatUnset,
				Currency = string.Empty
			});
		}

		public static void EncodeHistoricalPriceDataRecordResponse(this IMessageEncoder encoder, int requestId, DateTime startDateTime, double lastPrice, bool isFinalRecord)
		{
			encoder.EncodeHistoricalPriceDataRecordResponse(requestId, startDateTime, 0, 0, 0, lastPrice, 0, 0, 0, 0, isFinalRecord);
		}
	}
}