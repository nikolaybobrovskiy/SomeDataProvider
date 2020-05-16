// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	class MessageDecoder : Binary.MessageDecoder
	{
		MessageDecoder(byte[] buffer, int offset, int size)
			: base(buffer, offset, size)
		{
		}

		public override DtcProtocol.LogonRequest DecodeLogonRequest()
		{
			var r = GetRequest<LogonRequest>();
			return new DtcProtocol.LogonRequest(
				r.HeartbeatIntervalInSeconds,
				r.GetClientName(BufferSpan),
				r.GetHardwareIdentifier(BufferSpan));
		}

		public override DtcProtocol.HistoricalPriceDataRequest DecodeHistoricalPriceDataRequest()
		{
			var r = GetRequest<HistoricalPriceDataRequest>();
			return new DtcProtocol.HistoricalPriceDataRequest(
				r.RequestId,
				r.GetSymbol(BufferSpan),
				r.GetExchange(BufferSpan),
				r.RecordInterval,
				r.UseZLibCompression == 1);
		}

		public override DtcProtocol.MarketDataRequest DecodeMarketDataRequest()
		{
			var r = GetRequest<MarketDataRequest>();
			return new DtcProtocol.MarketDataRequest(
				r.RequestAction,
				r.SymbolId,
				r.GetSymbol(BufferSpan),
				r.GetExchange(BufferSpan),
				r.IntervalForSnapshotUpdatesInMilliseconds);
		}

		public new sealed class Factory : IMessageDecoderFactory
		{
			public IMessageDecoder CreateMessageDecoder(byte[] buffer, int offset, int size)
			{
				return new MessageDecoder(buffer, offset, size);
			}
		}
	}
}