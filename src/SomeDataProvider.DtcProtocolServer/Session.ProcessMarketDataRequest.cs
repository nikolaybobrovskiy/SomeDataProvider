// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Threading;
using System.Threading.Tasks;

using Dtc.Protocol;

using Microsoft.Extensions.Logging;

using NBLib.Logging;

using SomeDataProvider.DtcProtocolServer.DtcProtocol;

partial class Session
{
	async Task ProcessMarketDataRequestAsync(IMessageDecoder decoder, IMessageEncoder encoder, CancellationToken ct)
	{
		uint symbolId = 0;
		try
		{
			await L.LogOperationAsync(async () =>
			{
				var marketDataRequest = decoder.DecodeMarketDataRequest();
				symbolId = marketDataRequest.SymbolId;
				L.LogInformation("MarketDataRequest: {marketDataRequest}", marketDataRequest);

				if (marketDataRequest.RequestAction != RequestActionEnum.SUBSCRIBE)
				{
					throw new NotSupportedException($"MarketDataRequestAction '{marketDataRequest.RequestAction}' is not supported.");
				}
				var getSymbolsStoreResult = await _symbolsStoreProvider.GetSymbolsStoreAsync(marketDataRequest.Symbol, marketDataRequest.Exchange, ct);
				if (getSymbolsStoreResult == null)
				{
					L.LogInformation("Answer: MarketDataReject: NoSymbolStore");
					encoder.EncodeMarketDataReject(marketDataRequest.SymbolId, "Symbol is unknown or T-Invest source is not configured.");
				}
				else
				{
					var (symbolsStore, symbolCode) = getSymbolsStoreResult.Value;
					var symbol = await symbolsStore.GetSymbolAsync(symbolCode, ct);
					if (symbol == null)
					{
						L.LogInformation("Answer: MarketDataReject: NoSymbol");
						encoder.EncodeMarketDataReject(marketDataRequest.SymbolId, $"Symbol is unknown: {marketDataRequest.Symbol}.");
					}
					else if (symbol.DataService == SomeDataProvider.DataStorage.Definitions.DataService.TInvest)
					{
						encoder.EncodeMarketDataReject(marketDataRequest.SymbolId, "T-Invest market data is not implemented in this stage.");
					}
					else if (!symbol.IsRealTime)
					{
						L.LogInformation("Answer: MarketDataReject: NoRealTimeSupport");
						encoder.EncodeMarketDataReject(marketDataRequest.SymbolId, $"Real-time market data is not supported for {marketDataRequest.Symbol}.");
					}
					else
					{
						L.LogInformation("Answer: MarketDataSnapshot");
						encoder.EncodeMarketDataSnapshot(marketDataRequest.SymbolId, TradingStatusEnum.TRADING_STATUS_UNKNOWN, DateTime.Now);
					}
				}
				SendResponse(encoder.GetEncodedMessage());
			}, "ProcessMarketDataRequest()");
		}
		catch (SomeDataProvider.DataStorage.TInvest.TInvestRequestException ex)
		{
			encoder.EncodeMarketDataReject(symbolId, ex.Message);
			SendResponse(encoder.GetEncodedMessage());
		}
	}
}
