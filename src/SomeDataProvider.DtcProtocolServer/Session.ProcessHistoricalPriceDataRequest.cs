// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System.Threading;
using System.Threading.Tasks;

using Dtc.Protocol;

using Microsoft.Extensions.Logging;

using NBLib.Logging;

using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DtcProtocolServer.DtcProtocol;

partial class Session
{
	async Task ProcessHistoricalPriceDataRequestAsync(IMessageDecoder decoder, IMessageEncoder encoder, CancellationToken ct)
	{
		await L.LogOperationAsync(async () =>
		{
			var historicalPriceDataRequest = decoder.DecodeHistoricalPriceDataRequest();
			L.LogInformation("RequestedHistory: {historicalPriceDataRequest}", historicalPriceDataRequest);
			var getSymbolsStoreResult = await _symbolsStoreProvider.GetSymbolsStoreAsync(historicalPriceDataRequest.Symbol, ct);
			if (getSymbolsStoreResult == null)
			{
				L.LogInformation("Answer: HistoricalPriceDataReject: NoSymbolStore");
				encoder.EncodeHistoricalPriceDataReject(historicalPriceDataRequest.RequestId, HistoricalPriceDataRejectReasonCodeEnum.HPDR_GENERAL_REJECT_ERROR, $"Symbol store is found: {historicalPriceDataRequest.Symbol}.");
				SendResponse(encoder.GetEncodedMessage());
			}
			else
			{
				var (symbolsStore, symbolCode) = getSymbolsStoreResult.Value;
				var symbol = await symbolsStore.GetSymbolAsync(symbolCode, ct);
				if (symbol == null)
				{
					L.LogInformation("Answer: HistoricalPriceDataReject: NoSymbol");
					encoder.EncodeHistoricalPriceDataReject(historicalPriceDataRequest.RequestId, HistoricalPriceDataRejectReasonCodeEnum.HPDR_GENERAL_REJECT_ERROR, $"Symbol is unknown: {symbolCode}.");
					SendResponse(encoder.GetEncodedMessage());
				}
				else
				{
					await StreamHistoricalPriceData(historicalPriceDataRequest, symbol, ct);
				}
			}
		}, "ProcessHistoricalPriceDataRequest()");
	}

	async Task StreamHistoricalPriceData(HistoricalPriceDataRequest historicalPriceDataRequest, ISymbol symbol, CancellationToken ct)
	{
		await L.LogOperationAsync(async () =>
		{
			var historyInterval = historicalPriceDataRequest.RecordInterval switch
			{
				HistoricalDataIntervalEnum.INTERVAL_TICK => HistoryInterval.Tick,
				< HistoricalDataIntervalEnum.INTERVAL_1_DAY => HistoryInterval.Intraday,
				_ => HistoryInterval.Daily
			};
			var store = await _symbolHistoryStoreProvider.GetSymbolHistoryStoreAsync(symbol, historyInterval, ct);
			using var reader = await store.CreateSymbolHistoryReaderAsync(
				symbol,
				historyInterval,
				historicalPriceDataRequest.StartDateTime,
				historicalPriceDataRequest.EndDateTime,
				HistoryDownloadBatchSize,
				ct);
			await HistoricalPriceDataStreamer.StreamAsync(
				reader,
				historicalPriceDataRequest.RequestId,
				historicalPriceDataRequest.RecordInterval,
				_currentMessageProtocol.MessageEncoderFactory,
				bytes => SendResponse(bytes),
				L,
				ct);
		}, "StreamHistoricalPriceData({symbol})", symbol);
	}
}