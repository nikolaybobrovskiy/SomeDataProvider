// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Data;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Dtc.Protocol.Private;

/// <summary>
/// The data behind the private series extension v1: structured series metadata and observations by real-time period. The
/// extension's specification is docs/dtc-series-extension.md in MrktPltfrm; its messages are those of
/// <c>Dtc.Protocol.Private</c>.
/// </summary>
interface ISeriesSource
{
	/// <summary>Reads the metadata of a symbol, such as <c>fred-GDPC1.pch</c>.</summary>
	/// <returns>The metadata, without RequestID, Symbol, and Exchange, which the caller sets.</returns>
	/// <exception cref="SeriesSourceException">The source does not know the symbol, refuses the request, or failed.</exception>
	Task<SeriesMetadataResponse> GetSeriesMetadataAsync(string symbol, CancellationToken cancellationToken);

	/// <summary>
	/// Reads the observations of a symbol by real-time period, within the request's windows, in batches: in ascending
	/// observation date and then ascending real-time start, and without overlapping real-time periods within an observation.
	/// </summary>
	/// <param name="request">The request's symbol and windows; a null bound means the earliest or latest available.</param>
	/// <param name="cancellationToken">Cancels the reading.</param>
	/// <remarks>
	/// The source reads a batch only when the caller asks for it, so the caller can send each batch before the next is read. A
	/// <see cref="SeriesSourceException"/> can end the enumeration at any batch, including the first.
	/// </remarks>
	IAsyncEnumerable<IReadOnlyList<SeriesVintage>> GetSeriesVintagesAsync(SeriesVintagesRequest request, CancellationToken cancellationToken);
}

/// <summary>One value of one observation during one real-time period, both ends inclusive; a null value is missing.</summary>
/// <param name="ObservationDate">The observation's date.</param>
/// <param name="RealtimeStart">The first day the value was current, or the requested real-time start when the source clipped it.</param>
/// <param name="RealtimeEnd">The last day the value was current; 9999-12-31 while it still is.</param>
/// <param name="Value">The value; null when the source has none in this real-time period, such as FRED's <c>.</c>.</param>
readonly record struct SeriesVintage(DateOnly ObservationDate, DateOnly RealtimeStart, DateOnly RealtimeEnd, double? Value);

/// <summary>A series source refuses a request or failed; the server answers with <c>SERIES_REQUEST_REJECT</c>.</summary>
sealed class SeriesSourceException : Exception
{
	/// <summary>The longest retry hint a reject carries: 3,600 seconds.</summary>
	public const int MaxRetryAfterInSeconds = 3_600;

	public SeriesSourceException()
		: this(SeriesRequestRejectReason.SourceUnavailable, "The series source failed.")
	{
	}

	public SeriesSourceException(string message)
		: this(SeriesRequestRejectReason.SourceUnavailable, message)
	{
	}

	public SeriesSourceException(string message, Exception innerException)
		: this(SeriesRequestRejectReason.SourceUnavailable, message, null, innerException)
	{
	}

	/// <param name="reason">The reject's reason: a reason the extension defines, not <c>Unset</c> or <c>Unknown</c>.</param>
	/// <param name="message">The reject's text: why the request failed, without secrets.</param>
	/// <param name="retryAfter">
	/// For <see cref="SeriesRequestRejectReason.SourceUnavailable"/>, a hint of when a retry may succeed; clamped to whole
	/// seconds from 1 to 3,600, and ignored for other reasons.
	/// </param>
	/// <param name="innerException">The exception that caused this one, if any.</param>
	public SeriesSourceException(SeriesRequestRejectReason reason, string message, TimeSpan? retryAfter = null, Exception? innerException = null)
		: base(message, innerException)
	{
		if (reason is SeriesRequestRejectReason.Unset or SeriesRequestRejectReason.Unknown || !Enum.IsDefined(reason))
			throw new ArgumentOutOfRangeException(nameof(reason), reason, "The reject reason must be one the series extension defines.");
		Reason = reason;
		RetryAfter = reason == SeriesRequestRejectReason.SourceUnavailable && retryAfter is { } delay
			? TimeSpan.FromSeconds(Math.Clamp(Math.Ceiling(delay.TotalSeconds), 1, MaxRetryAfterInSeconds))
			: null;
	}

	/// <summary>The reject's reason.</summary>
	public SeriesRequestRejectReason Reason { get; }

	/// <summary>For <see cref="SeriesRequestRejectReason.SourceUnavailable"/>, whole seconds from 1 to 3,600; otherwise null.</summary>
	public TimeSpan? RetryAfter { get; }
}
