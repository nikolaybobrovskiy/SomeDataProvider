// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using NBLib.BuiltInTypes;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	using EncodeSecurityDefinitionResponseArgs = SomeDataProvider.DtcProtocolServer.DtcProtocol.IMessageEncoder.EncodeSecurityDefinitionResponseArgs;

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

		public override void EncodeSecurityDefinitionReject(int requestId, string rejectText)
		{
			var securityDefinitionReject = new SecurityDefinitionReject(requestId);
			var bytes = new byte[securityDefinitionReject.BaseSize + rejectText.GetVlsFieldLength()];
			securityDefinitionReject.SetRejectText(rejectText, bytes);
			Bytes = StructConverter.StructToBytesArray(securityDefinitionReject, bytes);
		}

		public override void EncodeSecurityDefinitionResponse(EncodeSecurityDefinitionResponseArgs args)
		{
			var securityDefinitionResponse = new SecurityDefinitionResponse(args.RequestId, args.IsFinalMessage ? (byte)1 : (byte)0);
			var bytes = new byte[securityDefinitionResponse.BaseSize
				+ args.Symbol.GetVlsFieldLength()
				+ args.Exchange.GetVlsFieldLength()
				+ args.Description.GetVlsFieldLength()
				+ args.Currency.GetVlsFieldLength()];
			securityDefinitionResponse.PriceDisplayFormat = args.PriceDisplayFormat;
			securityDefinitionResponse.SetSymbol(args.Symbol, bytes);
			securityDefinitionResponse.SetExchange(args.Exchange, bytes);
			securityDefinitionResponse.SetDescription(args.Description, bytes);
			securityDefinitionResponse.SetCurrency(args.Currency, bytes);
			securityDefinitionResponse.IsDelayed = args.IsDelayed ? (byte)1 : (byte)0;
			securityDefinitionResponse.PriceDisplayFormat = args.PriceDisplayFormat;
			securityDefinitionResponse.MinPriceIncrement = args.MinPriceIncrement;
			Bytes = StructConverter.StructToBytesArray(securityDefinitionResponse, bytes);
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