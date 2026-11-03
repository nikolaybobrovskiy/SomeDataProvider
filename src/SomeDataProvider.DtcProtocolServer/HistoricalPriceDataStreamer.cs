// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Threading;
using System.Threading.Tasks;

using Dtc.Protocol;

using Microsoft.Extensions.Logging;

using NBLib.CodeFlow;

using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DtcProtocolServer.DtcProtocol;

/// <summary>
/// Sends one historical price data response: exactly one HISTORICAL_PRICE_DATA_RESPONSE_HEADER, then the records, one
/// send per store batch.
/// </summary>
/// <remarks>
/// <para>
/// When there are records, the last one sent has <c>IsFinalRecord</c> = 1 and no other record has it. When there are
/// none, the header sets <c>NoRecordsToReturn</c> = 1 and ends the response: DTC sends no records after such a header.
/// </para>
/// <para>
/// A store batch can be empty while more follow (a FRED page of missing values only) or be the empty page after an
/// exact multiple of the batch size. So the streamer reads ahead to the next non-empty batch before sending one, which
/// tells it whether that batch holds the final record. Every message is encoded with a new encoder, because an encoder
/// appends each message it encodes to its buffer.
/// </para>
/// </remarks>
static class HistoricalPriceDataStreamer
{
	public static async Task StreamAsync(
		ISymbolHistoryStoreReader reader,
		int requestId,
		HistoricalDataIntervalEnum recordInterval,
		IMessageEncoderFactory encoderFactory,
		Action<byte[]> send,
		ILogger logger,
		CancellationToken ct,
		Action? headerSent = null)
	{
		// A tick answer knows its size from the pre-header count, so its header goes out before the first archive is
		// loaded; a load that fails or runs out of time after that closes the connection, never a late reject.
		var countedTicks = reader as TInvestTickReader;
		var batch = countedTicks != null ? null : await ReadNextNonEmptyBatchAsync(reader, logger, ct);
		var noRecordsToReturn = countedTicks != null ? countedTicks.Total == 0 : batch == null;
		logger.LogInformation("SendHeader({recordsExist})", !noRecordsToReturn);
		// TODO: ZLibCompression if client requests.
		await EncodeAndSendAsync(
			encoderFactory,
			send,
			encoder => encoder.EncodeHistoricalPriceDataResponseHeader(requestId, recordInterval, false, noRecordsToReturn, 1));
		headerSent?.Invoke();
		if (countedTicks != null)
		{
			await StreamCountedTicksAsync(countedTicks, requestId, encoderFactory, send, logger, ct);
			return;
		}

		while (batch != null)
		{
			if (reader is TInvestHistoryReader timed) timed.ReadDeadline = timed.Clock.GetUtcNow().AddSeconds(20);
			var nextBatch = batch.HasMore ? await ReadNextNonEmptyBatchAsync(reader, logger, ct) : null;
			var isFinalBatch = nextBatch == null;
			var records = batch.Records;
			logger.LogInformation("EncodeAndSendRecords({recordsCount}, {isFinalBatch})", records.Count, isFinalBatch);
			await EncodeAndSendAsync(encoderFactory, send, encoder =>
			{
				var recIndex = 0;
				var lastIndex = records.Count - 1;
				foreach (var rec in records)
				{
					if (rec is TInvestTickRecord tick)
					{
						encoder.EncodeHistoricalPriceDataTickRecordResponse(requestId, tick.Microseconds, tick.Direction == TInvestDirection.Buy, tick.Direction == TInvestDirection.Sell, tick.LastPrice, tick.Volume, isFinalBatch && recIndex == lastIndex);
						recIndex++;
						continue;
					}

					encoder.EncodeHistoricalPriceDataRecordResponse(
						requestId,
						rec.TimeStamp,
						rec.OpenPrice,
						rec.HighPrice,
						rec.LowPrice,
						rec.LastPrice,
						rec.Volume,
						0,
						0,
						0,
						isFinalBatch && recIndex == lastIndex);
					recIndex++;
				}
			});
			batch = nextBatch;
		}
	}

	/// <summary>
	/// Sends the ticks of a counted answer. There is no read-ahead: the final flag comes from the counted total, and each
	/// batch is sent before the next day is loaded, so a first tick waits for one load only.
	/// </summary>
	static async Task StreamCountedTicksAsync(TInvestTickReader reader, int requestId, IMessageEncoderFactory encoderFactory, Action<byte[]> send, ILogger logger, CancellationToken ct)
	{
		long sent = 0;
		while (sent < reader.Total)
		{
			var batch = await ReadNextNonEmptyBatchAsync(reader, logger, ct);
			if (batch == null) throw new TInvestRequestException("T-Invest tick history changed while it was read.");
			var records = batch.Records;
			var isFinalBatch = sent + records.Count >= reader.Total;
			logger.LogInformation("EncodeAndSendRecords({recordsCount}, {isFinalBatch})", records.Count, isFinalBatch);
			await EncodeAndSendAsync(encoderFactory, send, encoder =>
			{
				var i = 0;
				var lastIndex = records.Count - 1;
				foreach (var record in records)
				{
					var tick = (TInvestTickRecord)record;
					encoder.EncodeHistoricalPriceDataTickRecordResponse(requestId, tick.Microseconds, tick.Direction == TInvestDirection.Buy, tick.Direction == TInvestDirection.Sell, tick.LastPrice, tick.Volume, isFinalBatch && i == lastIndex);
					i++;
				}
			});
			sent += records.Count;
		}
	}

	/// <summary>Reads batches until one has records, and returns it, or returns null when the history ends first.</summary>
	static async Task<SymbolHistoryResponse?> ReadNextNonEmptyBatchAsync(ISymbolHistoryStoreReader reader, ILogger logger, CancellationToken ct)
	{
		while (true)
		{
			var batch = await reader.ReadSymbolHistoryAsync(ct);
			logger.LogDebug("BatchRead({recordsCount}, HasMore: {hasMore})", batch.Records.Count, batch.HasMore);
			if (batch.Records.Count > 0)
				return batch;
			if (!batch.HasMore)
				return null;
		}
	}

	static async Task EncodeAndSendAsync(IMessageEncoderFactory encoderFactory, Action<byte[]> send, Action<IMessageEncoder> encode)
	{
		var encoder = encoderFactory.CreateMessageEncoder();
		try
		{
			encode(encoder);
			send(encoder.GetEncodedMessage());
		}
		finally
		{
			await encoder.TryDisposeAsync();
		}
	}
}
