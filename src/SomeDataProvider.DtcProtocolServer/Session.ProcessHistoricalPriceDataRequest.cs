// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Threading;
using System.Threading.Tasks;

using Dtc.Protocol;

using Microsoft.Extensions.Logging;

using NBLib.Logging;

using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DtcProtocolServer.DtcProtocol;

partial class Session
{
	async Task ProcessHistoricalPriceDataRequestAsync(IMessageDecoder decoder, IMessageEncoder encoder, CancellationToken ct)
	{
		var requestId = 0;
		var historyDeadline = _timeProvider.GetUtcNow().AddSeconds(20);
		try
		{
			await L.LogOperationAsync(async () =>
			{
				var historicalPriceDataRequest = decoder.DecodeHistoricalPriceDataRequest();
				requestId = historicalPriceDataRequest.RequestId;
				L.LogInformation("RequestedHistory: {historicalPriceDataRequest}", historicalPriceDataRequest);

				var getSymbolsStoreResult = await _symbolsStoreProvider.GetSymbolsStoreAsync(historicalPriceDataRequest.Symbol, historicalPriceDataRequest.Exchange, ct);
				if (getSymbolsStoreResult == null)
				{
					L.LogInformation("Answer: HistoricalPriceDataReject: NoSymbolStore");
					encoder.EncodeHistoricalPriceDataReject(historicalPriceDataRequest.RequestId, HistoricalPriceDataRejectReasonCodeEnum.HPDR_GENERAL_REJECT_ERROR, "Symbol is unknown or T-Invest source is not configured.");
					SendResponse(encoder.GetEncodedMessage());
				}
				else
				{
					var (symbolsStore, symbolCode) = getSymbolsStoreResult.Value;
					var symbol = await symbolsStore.GetSymbolAsync(symbolCode, ct);
					if (symbol == null)
					{
						L.LogInformation("Answer: HistoricalPriceDataReject: NoSymbol");
						encoder.EncodeHistoricalPriceDataReject(historicalPriceDataRequest.RequestId, HistoricalPriceDataRejectReasonCodeEnum.HPDR_GENERAL_REJECT_ERROR, $"Symbol is unknown: {historicalPriceDataRequest.Symbol}.");
						SendResponse(encoder.GetEncodedMessage());
					}
					else if (symbol.DataService == DataService.TInvest)
					{
						await StreamTInvestHistoryAsync(historicalPriceDataRequest, (TInvestSymbol)symbol, (TInvestStore)symbolsStore, historyDeadline, ct);
					}
					else
					{
						await StreamHistoricalPriceData(historicalPriceDataRequest, symbol, ct);
					}
				}
			}, "ProcessHistoricalPriceDataRequest()");
		}
		catch (SomeDataProvider.DataStorage.TInvest.TInvestRequestException ex)
		{
			var limited = ex.Kind is TInvestFailureKind.Limit or TInvestFailureKind.Starting;
			var retry = System.Text.RegularExpressions.Regex.Match(ex.Message, @"retry in ([0-9]+) s");
			var seconds = retry.Success ? Math.Clamp(int.Parse(retry.Groups[1].Value), 1, 300) : 1;
			encoder.EncodeHistoricalPriceDataReject(requestId, limited ? HistoricalPriceDataRejectReasonCodeEnum.HPDR_UNABLE_TO_SERVE_DATA_RETRY_IN_SPECIFIED_SECONDS : HistoricalPriceDataRejectReasonCodeEnum.HPDR_GENERAL_REJECT_ERROR, ex.Message, limited ? seconds : 0);
			SendResponse(encoder.GetEncodedMessage());
		}
	}

	async Task StreamTInvestHistoryAsync(HistoricalPriceDataRequest request, TInvestSymbol symbol, TInvestStore store, DateTimeOffset deadline, CancellationToken ct)
	{
		var sentHeader = false;
		try
		{
			using ISymbolHistoryStoreReader reader = request.RecordInterval == HistoricalDataIntervalEnum.INTERVAL_TICK
				? await store.CreateTickReaderAsync(symbol, request.StartDateTime, request.EndDateTime, deadline, ct)
				: await store.CreateCandleReaderAsync(symbol, (int)request.RecordInterval, request.StartDateTime, request.EndDateTime, deadline, ct);
			if (reader is TInvestHistoryReader candles) candles.ReadDeadline = deadline;
			await HistoricalPriceDataStreamer.StreamAsync(reader, request.RequestId, request.RecordInterval,
				_currentMessageProtocol.MessageEncoderFactory, bytes => SendResponse(bytes), L, ct, () => sentHeader = true);
		}
		catch (Exception ex) when (sentHeader)
		{
			L.LogWarning("T-Invest history connection closed after header; failure kind {kind}.", ex is TInvestRequestException bridge ? bridge.Kind.ToString() : ex.GetType().Name);
			CloseSession();
		}
		catch (Exception ex) when (ex is not TInvestRequestException && !ct.IsCancellationRequested)
		{
			// Do not include exception messages: they can contain upstream data or credentials.
			L.LogWarning("T-Invest history failed before header; exception type {type}.", ex.GetType().Name);
			throw new TInvestRequestException("T-Invest source request failed.");
		}
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
