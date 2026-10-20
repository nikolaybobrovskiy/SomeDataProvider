// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Dtc.Protocol.Private;

using Microsoft.Extensions.Logging;

using NBLib.CodeFlow;
using NBLib.Exceptions;
using NBLib.Logging;

using SomeDataProvider.DtcProtocolServer.Data;
using SomeDataProvider.DtcProtocolServer.DtcProtocol;

/// <summary>
/// Sends the answer to one <c>SERIES_VINTAGES_REQUEST</c> of the private series extension: the rows, one
/// <c>SERIES_VINTAGE_RECORD</c> each, and then exactly one terminator; or exactly one <c>SERIES_REQUEST_REJECT</c>.
/// </summary>
/// <remarks>
/// <para>
/// The rows go out batch by batch, as the source reads them, so the client receives each batch before the source reads
/// the next: the client waits at most its request timeout for each next message, not for the whole answer. Each send holds
/// at most <see cref="RowsPerSend"/> rows.
/// </para>
/// <para>
/// Before a batch is sent, its rows are checked against the extension's rules: ascending observation date and then
/// real-time start, real-time start not after real-time end, no overlap within an observation, and a finite value below
/// <c>DBL_MAX</c>. A batch that breaks them, or a source failure, ends the answer with a reject instead of the terminator,
/// even after rows went out, because a terminator would claim the answer complete. A canceled request, or a connection
/// that is closing, sends nothing more.
/// </para>
/// </remarks>
static class SeriesVintagesStreamer
{
	/// <summary>The most rows one send holds: 1,700 rows of 48 bytes, 81,600 bytes, below the large object heap's 85,000.</summary>
	internal const int RowsPerSend = 1_700;

	/// <param name="batches">The rows, as <see cref="ISeriesSource.GetSeriesVintagesAsync"/> returns them.</param>
	/// <param name="requestId">The request's RequestID, which every message of the answer carries.</param>
	/// <param name="encoderFactory">Creates an encoder for each send.</param>
	/// <param name="send">Sends encoded messages; false when the connection is closing, which ends the answer.</param>
	/// <param name="logger">Logs the answer.</param>
	/// <param name="cancellationToken">Canceled when the request must stop, such as when the client disconnects.</param>
	public static async Task StreamAsync(
		IAsyncEnumerable<IReadOnlyList<SeriesVintage>> batches,
		int requestId,
		IMessageEncoderFactory encoderFactory,
		Func<byte[], bool> send,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		var checker = default(RowChecker);
		var rowsSent = 0;
		try
		{
			await foreach (var batch in batches.WithCancellation(cancellationToken))
			{
				checker.Check(batch);
				for (var start = 0; start < batch.Count; start += RowsPerSend)
				{
					var count = Math.Min(RowsPerSend, batch.Count - start);
					var first = start;
					if (!await EncodeAndSendAsync(encoderFactory, send, encoder => encoder.EncodeSeriesVintageRecords(requestId, batch, first, count)))
					{
						logger.LogInformation("SeriesVintages: the connection is closing after {rowsSent} rows.", rowsSent);
						return;
					}
					rowsSent += count;
				}
			}
		}
		catch (SeriesSourceException ex)
		{
			logger.LogInformation("Answer: SeriesRequestReject: {reason} after {rowsSent} rows: {rejectText}", ex.Reason, rowsSent, ex.Message);
			await SendRejectAsync(encoderFactory, send, requestId, ex.Reason, ex.Message, ex.RetryAfter);
			return;
		}
		catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
		{
			logger.LogException(ex, LogLevel.Warning, "Reading the vintages failed.");
			await SendRejectAsync(encoderFactory, send, requestId, SeriesRequestRejectReason.SourceUnavailable, "The provider failed while reading the vintages.", null);
			return;
		}
		logger.LogInformation("Answer: SeriesVintageRecord x{rowsSent} and the terminator", rowsSent);
		await EncodeAndSendAsync(encoderFactory, send, encoder => encoder.EncodeSeriesVintagesTerminator(requestId));
	}

	/// <summary>Sends <c>SERIES_REQUEST_REJECT</c> for a series request.</summary>
	public static Task<bool> SendRejectAsync(
		IMessageEncoderFactory encoderFactory,
		Func<byte[], bool> send,
		int requestId,
		SeriesRequestRejectReason reason,
		string rejectText,
		TimeSpan? retryAfter)
	{
		var reject = new SeriesRequestReject
		{
			RequestID = requestId,
			RejectReason = reason,
			RetryAfter = reason == SeriesRequestRejectReason.SourceUnavailable ? retryAfter : null,
			RejectText = rejectText,
		};
		return EncodeAndSendAsync(encoderFactory, send, encoder => encoder.EncodeSeriesRequestReject(reject));
	}

	/// <summary>Whether an exception only reports that the request was canceled.</summary>
	internal static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
		exception.IsExplainedCancellation() || (exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

	/// <summary>Encodes with a new encoder, sends, and disposes the encoder; an encoding failure sends nothing.</summary>
	internal static async Task<bool> EncodeAndSendAsync(IMessageEncoderFactory encoderFactory, Func<byte[], bool> send, Action<IMessageEncoder> encode)
	{
		// Every send gets a new encoder, because an encoder appends each message it encodes to its buffer.
		var encoder = encoderFactory.CreateMessageEncoder();
		try
		{
			encode(encoder);
			return send(encoder.GetEncodedMessage());
		}
		finally
		{
			await encoder.TryDisposeAsync();
		}
	}

	/// <summary>Checks rows against the extension's rules, across batches.</summary>
	struct RowChecker
	{
		SeriesVintage _last;
		bool _hasLast;

		/// <exception cref="SeriesSourceException">A row breaks the rules; the reason is <see cref="SeriesRequestRejectReason.SourceUnavailable"/>.</exception>
		public void Check(IReadOnlyList<SeriesVintage> batch)
		{
			foreach (var row in batch)
			{
				if (row.RealtimeStart > row.RealtimeEnd)
					throw Invalid($"observation {Iso(row.ObservationDate)} has real-time start {Iso(row.RealtimeStart)} after its end {Iso(row.RealtimeEnd)}");
				if (row.Value is { } value && (!double.IsFinite(value) || value == double.MaxValue))
					throw Invalid($"observation {Iso(row.ObservationDate)} has the value {value.ToString(CultureInfo.InvariantCulture)}, which the extension cannot send");
				if (_hasLast)
				{
					if (row.ObservationDate < _last.ObservationDate)
						throw Invalid($"observation {Iso(row.ObservationDate)} follows observation {Iso(_last.ObservationDate)}");
					if (row.ObservationDate == _last.ObservationDate && row.RealtimeStart <= _last.RealtimeEnd)
						throw Invalid($"observation {Iso(row.ObservationDate)} has a real-time period from {Iso(row.RealtimeStart)} that is not after the previous one, which ends on {Iso(_last.RealtimeEnd)}");
				}
				_last = row;
				_hasLast = true;
			}
		}

		static SeriesSourceException Invalid(string problem) =>
			new(SeriesRequestRejectReason.SourceUnavailable, $"The source returned rows the extension cannot send: {problem}.");

		static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
	}
}
