// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using SomeDataProvider.DtcProtocolServer.DtcProtocol;
using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

partial class Session
{
	async Task ProcessMarketDataRequestAsync(IMessageDecoder decoder, IMessageEncoder encoder, CancellationToken ct)
	{
		var marketDataRequest = decoder.DecodeMarketDataRequest();
		L.LogInformation("MarketDataRequest: {marketDataRequest}", marketDataRequest);
		if (marketDataRequest.RequestAction != RequestActionEnum.Subscribe)
		{
			throw new NotSupportedException($"MarketDataRequestAction '{marketDataRequest.RequestAction}' is not supported.");
		}
		var symbol = await _symbolsStore.GetSymbolAsync(marketDataRequest.Symbol, ct);
		if (symbol == null)
		{
			L.LogInformation("Answer: MarketDataReject: NoSymbol");
			encoder.EncodeMarketDataReject(marketDataRequest.SymbolId, $"Symbol is unknown: {marketDataRequest.Symbol}.");
		}
		else if (!symbol.IsRealTime)
		{
			L.LogInformation("Answer: MarketDataReject: NoRealTimeSupport");
			encoder.EncodeMarketDataReject(marketDataRequest.SymbolId, $"Real-time market data is not supported for {marketDataRequest.Symbol}.");
		}
		else
		{
			L.LogInformation("Answer: MarketDataSnapshot");
			encoder.EncodeMarketDataSnapshot(marketDataRequest.SymbolId, TradingStatusEnum.TradingStatusUnknown, DateTime.Now);
		}
		SendAsync(encoder.GetEncodedMessage());
	}
}