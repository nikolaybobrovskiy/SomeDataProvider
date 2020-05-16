// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using NBLib.BuiltInTypes;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class MessageEncoder : Binary.MessageEncoder
	{
		public override void EncodeLogonResponse(LogonStatusEnum logonStatus, string resultText, bool oneHistoricalPriceDataRequestPerConnection)
		{
			var logonResponse = new LogonResponse(logonStatus)
			{
				OneHistoricalPriceDataRequestPerConnection = oneHistoricalPriceDataRequestPerConnection ? (byte)1 : (byte)0
			};
			var bytes = new byte[logonResponse.BaseSize + resultText.GetVlsFieldLength()];
			logonResponse.SetResultText(resultText, bytes);
			Bytes = StructConverter.StructToBytesArray(logonResponse, bytes);
		}

		public override void EncodeHistoricalPriceDataReject(int requestId, HistoricalPriceDataRejectReasonCodeEnum rejectReasonCode, string rejectText)
		{
			var historicalPriceDataReject = new HistoricalPriceDataReject(requestId, rejectReasonCode);
			var bytes = new byte[historicalPriceDataReject.BaseSize + rejectText.GetVlsFieldLength()];
			historicalPriceDataReject.SetRejectText(rejectText, bytes);
			Bytes = StructConverter.StructToBytesArray(historicalPriceDataReject, bytes);
		}

		public override void EncodeMarketDataReject(uint symbolId, string rejectText)
		{
			var marketDataReject = new MarketDataReject(symbolId);
			var bytes = new byte[marketDataReject.BaseSize + rejectText.GetVlsFieldLength()];
			marketDataReject.SetRejectText(rejectText, bytes);
			Bytes = StructConverter.StructToBytesArray(marketDataReject, bytes);
		}

		public new sealed class Factory : IMessageEncoderFactory
		{
			public IMessageEncoder CreateMessageEncoder()
			{
				return new MessageEncoder();
			}
		}
	}
}