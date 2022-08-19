// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using NBLib.CodeFlow;
using NBLib.Logging;

using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DtcProtocolServer.DtcProtocol;
using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

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
				encoder.EncodeHistoricalPriceDataReject(historicalPriceDataRequest.RequestId, HistoricalPriceDataRejectReasonCodeEnum.HpdrGeneralRejectError, $"Symbol store is found: {historicalPriceDataRequest.Symbol}.");
				SendAsync(encoder.GetEncodedMessage());
			}
			else
			{
				var (symbolsStore, symbolCode) = getSymbolsStoreResult.Value;
				var symbol = await symbolsStore.GetSymbolAsync(symbolCode, ct);
				if (symbol == null)
				{
					L.LogInformation("Answer: HistoricalPriceDataReject: NoSymbol");
					encoder.EncodeHistoricalPriceDataReject(historicalPriceDataRequest.RequestId, HistoricalPriceDataRejectReasonCodeEnum.HpdrGeneralRejectError, $"Symbol is unknown: {symbolCode}.");
					SendAsync(encoder.GetEncodedMessage());
				}
				else
				{
					await StreamHistoricalPriceData(historicalPriceDataRequest, symbol, encoder, ct);
				}
			}
		}, "ProcessHistoricalPriceDataRequest()");
	}

	async Task StreamHistoricalPriceData(HistoricalPriceDataRequest historicalPriceDataRequest, ISymbol symbol, IMessageEncoder encoder, CancellationToken ct)
	{
		await L.LogOperationAsync(async () =>
		{
			var requestId = historicalPriceDataRequest.RequestId;
			var historyInterval = historicalPriceDataRequest.RecordInterval switch
			{
				HistoricalDataIntervalEnum.IntervalTick => HistoryInterval.Tick,
				< HistoricalDataIntervalEnum.Interval1Day => HistoryInterval.Intraday,
				_ => HistoryInterval.Daily
			};
			var store = await _symbolHistoryStoreProvider.GetSymbolHistoryStoreAsync(symbol, historyInterval, ct);
			var headerSent = false;
			var hasMore = false;
			using var reader = await store.CreateSymbolHistoryReaderAsync(
				symbol,
				historyInterval,
				historicalPriceDataRequest.StartDateTime,
				historicalPriceDataRequest.EndDateTime,
				HistoryDownloadBatchSize,
				ct);
			do
			{
				await L.LogOperationAsync(async () =>
				{
					// ReSharper disable once AccessToDisposedClosure
					var r = await reader.ReadSymbolHistoryAsync(ct);
					hasMore = r.HasMore;
					L.LogDebug("HasMore: {hasMore}", hasMore);
					if (!headerSent)
					{
						var noRecordsToReturn = r.Records.Count == 0;
						L.LogInformation("SendHeader({recordsExist})", !noRecordsToReturn);
						// TODO: ZLibCompression if client requests.
						encoder.EncodeHistoricalPriceDataResponseHeader(requestId, historicalPriceDataRequest.RecordInterval, false, noRecordsToReturn, 1);
						SendAsync(encoder.GetEncodedMessage());
					}
					var historyRecordsEncoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
					try
					{
						var recIndex = 0;
						var recsCount = r.Records.Count;
						L.LogInformation("EncodeAndSendRecords({recordsCount})", recsCount);
						foreach (var rec in r.Records)
						{
							historyRecordsEncoder.EncodeHistoricalPriceDataRecordResponse(
								requestId,
								rec.TimeStamp,
								rec.OpenPrice,
								rec.HighPrice,
								rec.LowPrice,
								rec.LastPrice,
								rec.Volume,
								0, 0, 0, !hasMore && recIndex == recsCount - 1);
							recIndex++;
						}
						SendAsync(historyRecordsEncoder.GetEncodedMessage());
					}
					finally
					{
						await historyRecordsEncoder.TryDisposeAsync();
					}
				}, "DownloadAndSendHistoryDataBatch");
			}
			// ReSharper disable once LoopVariableIsNeverChangedInsideLoop
			while (hasMore);
		}, "StreamHistoricalPriceData({symbol})", symbol);
	}
}