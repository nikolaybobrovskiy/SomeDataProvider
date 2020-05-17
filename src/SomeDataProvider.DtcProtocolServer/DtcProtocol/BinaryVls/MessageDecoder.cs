// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;

	using NBLib.DateTime;

	class MessageDecoder : Binary.MessageDecoder
	{
		MessageDecoder(Memory<byte> buffer)
			: base(buffer)
		{
		}

		public override DtcProtocol.LogonRequest DecodeLogonRequest()
		{
			var r = GetRequest<LogonRequest>();
			return new DtcProtocol.LogonRequest(
				r.HeartbeatIntervalInSeconds,
				r.GetClientName(Buffer.Span),
				r.GetHardwareIdentifier(Buffer.Span));
		}

		public override DtcProtocol.HistoricalPriceDataRequest DecodeHistoricalPriceDataRequest()
		{
			var r = GetRequest<HistoricalPriceDataRequest>();
			return new DtcProtocol.HistoricalPriceDataRequest(
				r.RequestId,
				r.GetSymbol(Buffer.Span),
				r.GetExchange(Buffer.Span),
				r.RecordInterval,
				Convert.ToDouble(r.StartDateTime).UnixTimeStampToDateTimeUtc(),
				Convert.ToDouble(r.EndDateTime).UnixTimeStampToDateTimeUtc(),
				r.MaxDaysToReturn,
				r.UseZLibCompression == 1);
		}

		public override DtcProtocol.MarketDataRequest DecodeMarketDataRequest()
		{
			var r = GetRequest<MarketDataRequest>();
			return new DtcProtocol.MarketDataRequest(
				r.RequestAction,
				r.SymbolId,
				r.GetSymbol(Buffer.Span),
				r.GetExchange(Buffer.Span),
				r.IntervalForSnapshotUpdatesInMilliseconds);
		}

		public override DtcProtocol.SecurityDefinitionForSymbolRequest DecodeSecurityDefinitionForSymbolRequest()
		{
			var r = GetRequest<SecurityDefinitionForSymbolRequest>();
			return new DtcProtocol.SecurityDefinitionForSymbolRequest(
				r.RequestId,
				r.GetSymbol(Buffer.Span),
				r.GetExchange(Buffer.Span));
		}

		public new sealed class Factory : IMessageDecoderFactory
		{
			public IMessageDecoder CreateMessageDecoder(Memory<byte> buffer)
			{
				return new MessageDecoder(buffer);
			}
		}
	}
}