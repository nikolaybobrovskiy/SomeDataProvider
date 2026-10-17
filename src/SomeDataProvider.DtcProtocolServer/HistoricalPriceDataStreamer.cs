// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using NBLib.CodeFlow;

using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DtcProtocolServer.DtcProtocol;
using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

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
		CancellationToken ct)
	{
		var batch = await ReadNextNonEmptyBatchAsync(reader, logger, ct);
		var noRecordsToReturn = batch == null;
		logger.LogInformation("SendHeader({recordsExist})", !noRecordsToReturn);
		// TODO: ZLibCompression if client requests.
		await EncodeAndSendAsync(
			encoderFactory,
			send,
			encoder => encoder.EncodeHistoricalPriceDataResponseHeader(requestId, recordInterval, false, noRecordsToReturn, 1));

		while (batch != null)
		{
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
