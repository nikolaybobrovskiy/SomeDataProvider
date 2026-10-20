// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Data;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Dtc.Protocol;
using Dtc.Protocol.Private;

using SomeDataProvider.DataStorage.Fred;
using SomeDataProvider.DataStorage.Fred.Dto;

/// <summary>
/// Answers the series extension from FRED: fred/series for metadata, and fred/series/observations with output type 1 for
/// vintages (docs/dtc-series-extension.md in MrktPltfrm, §5).
/// </summary>
/// <remarks>
/// <para>
/// Codes are FRED symbol codes without the <c>fred-</c> prefix, such as <c>GDPC1.pch</c>. Units and units short are
/// the base series'; the suffix only sets <see cref="SeriesMetadataResponse.UnitsTransformation"/>, and FRED applies it to
/// the vintages' values.
/// </para>
/// <para>
/// FRED's failures map to reject reasons: a missing series is <see cref="SeriesRequestRejectReason.UnknownSymbol"/>; any
/// other HTTP 400 that is not about the API key is <see cref="SeriesRequestRejectReason.InvalidRequest"/>, with FRED's
/// message; everything else, the API key included, is <see cref="SeriesRequestRejectReason.SourceUnavailable"/>, with
/// FRED's Retry-After or, for HTTP 429, one minute as the retry hint.
/// </para>
/// </remarks>
sealed partial class FredSeriesSource : ISeriesSource
{
	/// <summary>
	/// The rows asked of FRED at once: 25,000. Smaller pages reach the client sooner, which waits at most its request timeout
	/// for each next message; larger pages take fewer of FRED's 120 requests a minute.
	/// </summary>
	internal const int DefaultPageSize = 25_000;

	/// <summary>The retry hint after FRED's HTTP 429 without a Retry-After: FRED allows 120 requests a minute.</summary>
	internal static readonly TimeSpan RateLimitRetryAfter = TimeSpan.FromMinutes(1);

	/// <summary>The longest FRED message a reject quotes.</summary>
	const int MaxQuotedMessageLength = 500;

	/// <summary>The forms of FRED's <c>last_updated</c>: a UTC offset in hours, or in hours and minutes.</summary>
	static readonly string[] LastUpdatedFormats = { "yyyy-MM-dd HH:mm:sszz", "yyyy-MM-dd HH:mm:sszzz" };

	readonly Store _store;
	readonly int _pageSize;

	public FredSeriesSource(Store store, int pageSize = DefaultPageSize)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, Service.MaxObservationsLimit);
		_store = store;
		_pageSize = pageSize;
	}

	/// <param name="symbol">A FRED symbol code without the <c>fred-</c> prefix.</param>
	/// <param name="cancellationToken">Cancels the request.</param>
	public async Task<SeriesMetadataResponse> GetSeriesMetadataAsync(string symbol, CancellationToken cancellationToken)
	{
		var (seriesId, transformation) = ParseCode(symbol);
		SeriesDetails? details;
		try
		{
			details = await _store.GetSeriesDetailsAsync(seriesId, cancellationToken);
		}
		catch (Exception ex) when (IsSourceFailure(ex, cancellationToken))
		{
			throw ToSourceException(ex, seriesId);
		}
		if (details == null)
			throw UnknownSeries(seriesId);
		return ToMetadata(details, transformation);
	}

	/// <param name="request">The request, whose Symbol is a FRED symbol code without the <c>fred-</c> prefix.</param>
	/// <param name="cancellationToken">Cancels the reading.</param>
	public async IAsyncEnumerable<IReadOnlyList<SeriesVintage>> GetSeriesVintagesAsync(SeriesVintagesRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		var (seriesId, transformation) = ParseCode(request.Symbol);
		var pages = _store.ReadRealtimeObservationsAsync(
			seriesId,
			transformation,
			request.ObservationStart,
			request.ObservationEnd,
			request.RealtimeStart ?? Service.EarliestRealtimeDate,
			request.RealtimeEnd ?? Service.LatestRealtimeDate,
			_pageSize,
			cancellationToken);
		await using var enumerator = pages.GetAsyncEnumerator(cancellationToken);
		while (true)
		{
			bool hasPage;
			try
			{
				hasPage = await enumerator.MoveNextAsync();
			}
			catch (Exception ex) when (IsSourceFailure(ex, cancellationToken))
			{
				throw ToSourceException(ex, seriesId);
			}
			if (!hasPage)
				yield break;
			var page = enumerator.Current;
			var vintages = new SeriesVintage[page.Count];
			for (var i = 0; i < vintages.Length; i++)
			{
				var row = page[i];
				vintages[i] = new SeriesVintage(row.Date, row.RealtimeStart, row.RealtimeEnd, row.Value);
			}
			yield return vintages;
		}
	}

	/// <summary>Maps FRED's metadata to the extension's, by meaning.</summary>
	internal static SeriesMetadataResponse ToMetadata(SeriesDetails details, DataValueTransformation transformation)
	{
		var (frequency, weekEndDay) = ToFrequency(details.Frequency, details.FrequencyShort);
		return new SeriesMetadataResponse
		{
			Title = details.Title ?? string.Empty,
			Units = details.Units ?? string.Empty,
			UnitsShort = details.UnitsShort ?? string.Empty,
			Frequency = frequency,
			WeekEndDay = weekEndDay,
			UnitsTransformation = ToUnitsTransformation(transformation),
			SeasonalAdjustment = ToSeasonalAdjustment(details.SeasonalAdjustmentShort),
			ObservationStart = ParseDate(details.ObservationStart),
			ObservationEnd = ParseDate(details.ObservationEnd),
			LastUpdated = ParseLastUpdated(details.LastUpdated),
		};
	}

	/// <summary>
	/// Maps FRED's frequency, by its code (<c>frequency_short</c>) and name (<c>frequency</c>), such as Q and "Quarterly",
	/// or W and "Weekly, Ending Friday".
	/// </summary>
	/// <remarks>
	/// "Daily, 7-Day" is daily including weekends; other daily names, such as "Daily, Close", are daily. Weekly and biweekly
	/// series name the weekday that ends each period after "Ending" or "As of"; without one it is null. A fiscal-year
	/// frequency, or a code FRED has not documented, is <see cref="SeriesFrequency.Other"/>, because its periods are not the
	/// calendar periods the client derives.
	/// </remarks>
	internal static (SeriesFrequency Frequency, DayOfWeek? WeekEndDay) ToFrequency(string? frequency, string? frequencyShort)
	{
		var name = frequency ?? string.Empty;
		if (name.Contains("Fiscal", StringComparison.OrdinalIgnoreCase))
			return (SeriesFrequency.Other, null);
		return frequencyShort?.Trim().ToUpperInvariant() switch
		{
			"D" => (name.Contains("7-Day", StringComparison.OrdinalIgnoreCase) ? SeriesFrequency.DailyIncludingWeekends : SeriesFrequency.Daily, null),
			"W" => (SeriesFrequency.Weekly, ParseWeekEndDay(name)),
			"BW" => (SeriesFrequency.Biweekly, ParseWeekEndDay(name)),
			"M" => (SeriesFrequency.Monthly, null),
			"Q" => (SeriesFrequency.Quarterly, null),
			"SA" => (SeriesFrequency.Semiannual, null),
			"A" => (SeriesFrequency.Annual, null),
			_ => (SeriesFrequency.Other, null),
		};
	}

	/// <summary>Maps FRED's seasonal adjustment code (<c>seasonal_adjustment_short</c>): NSA, SA, SAAR, SSA or NA.</summary>
	internal static SeriesSeasonalAdjustment ToSeasonalAdjustment(string? seasonalAdjustmentShort) => seasonalAdjustmentShort?.Trim().ToUpperInvariant() switch
	{
		null or "" => SeriesSeasonalAdjustment.Unset,
		"NSA" => SeriesSeasonalAdjustment.NotSeasonallyAdjusted,
		"SA" => SeriesSeasonalAdjustment.SeasonallyAdjusted,
		"SAAR" => SeriesSeasonalAdjustment.SeasonallyAdjustedAnnualRate,
		"SSA" => SeriesSeasonalAdjustment.SmoothedSeasonallyAdjusted,
		"NA" => SeriesSeasonalAdjustment.NotApplicable,
		_ => SeriesSeasonalAdjustment.Other,
	};

	/// <summary>Maps a symbol's units suffix; no suffix is levels, FRED's <c>lin</c>.</summary>
	internal static SeriesUnitsTransformation ToUnitsTransformation(DataValueTransformation transformation) => transformation switch
	{
		DataValueTransformation.NoTransformation => SeriesUnitsTransformation.Levels,
		DataValueTransformation.Change => SeriesUnitsTransformation.Change,
		DataValueTransformation.ChangeFromYearAgo => SeriesUnitsTransformation.ChangeFromYearAgo,
		DataValueTransformation.PercentChange => SeriesUnitsTransformation.PercentChange,
		DataValueTransformation.PercentChangeFromYearAgo => SeriesUnitsTransformation.PercentChangeFromYearAgo,
		DataValueTransformation.CompoundedAnnualRateOfChange => SeriesUnitsTransformation.CompoundedAnnualRateOfChange,
		DataValueTransformation.ContinuouslyCompoundedRateOfChange => SeriesUnitsTransformation.ContinuouslyCompoundedRateOfChange,
		DataValueTransformation.ContinuouslyCompoundedAnnualRateOfChange => SeriesUnitsTransformation.ContinuouslyCompoundedAnnualRateOfChange,
		DataValueTransformation.NaturalLog => SeriesUnitsTransformation.NaturalLog,
		_ => throw new ArgumentOutOfRangeException(nameof(transformation), transformation, "The transformation has no units transformation code."),
	};

	/// <summary>A <c>yyyy-MM-dd</c> date; null when it is missing or not a date.</summary>
	internal static DateOnly? ParseDate(string? value) =>
		DateOnly.TryParseExact(value, Service.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

	/// <summary>
	/// FRED's <c>last_updated</c>, such as <c>2013-07-31 09:26:16-05</c>, converted to UTC; null when it is missing, not such
	/// an instant, or exactly the Unix epoch, which the extension cannot send because 0 means unset.
	/// </summary>
	internal static DtcTimestamp? ParseLastUpdated(string? value)
	{
		if (!DateTimeOffset.TryParseExact(value, LastUpdatedFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
			return null;
		var timestamp = DtcTimestamp.FromDateTimeOffset(instant);
		return timestamp.UnixMicroseconds == 0 ? null : timestamp;
	}

	/// <summary>Whether an exception is FRED's failure rather than the caller's cancellation or a defect of the provider.</summary>
	internal static bool IsSourceFailure(Exception exception, CancellationToken cancellationToken) => exception switch
	{
		OperationCanceledException => !cancellationToken.IsCancellationRequested,
		FredApiException or HttpRequestException or IOException => true,
		_ => false,
	};

	/// <summary>Maps FRED's failure to the reject that answers the request.</summary>
	internal static SeriesSourceException ToSourceException(Exception exception, SeriesInfoId seriesId)
	{
		switch (exception)
		{
			case FredApiException { IsSeriesNotFound: true }:
				return UnknownSeries(seriesId, exception);
			case FredApiException { StatusCode: HttpStatusCode.TooManyRequests } ex:
				return new SeriesSourceException(SeriesRequestRejectReason.SourceUnavailable, "FRED's request rate limit is exceeded.", ex.RetryAfter ?? RateLimitRetryAfter, ex);
			case FredApiException { IsApiKeyError: true } ex:
				return new SeriesSourceException(SeriesRequestRejectReason.SourceUnavailable, "FRED refuses the provider's API key; the provider's configuration must be fixed.", ex.RetryAfter, ex);
			case FredApiException { StatusCode: HttpStatusCode.BadRequest } ex:
				return new SeriesSourceException(SeriesRequestRejectReason.InvalidRequest, $"FRED refuses the request: {Quote(ex.ErrorMessage)}", null, ex);
			case FredApiException ex:
				return new SeriesSourceException(SeriesRequestRejectReason.SourceUnavailable, $"FRED failed with HTTP {((int)ex.StatusCode).ToString(CultureInfo.InvariantCulture)}: {Quote(ex.ErrorMessage)}", ex.RetryAfter, ex);
			case OperationCanceledException:
				return new SeriesSourceException(SeriesRequestRejectReason.SourceUnavailable, "FRED did not answer in time.", null, exception);
			default:
				return new SeriesSourceException(SeriesRequestRejectReason.SourceUnavailable, "FRED cannot be reached.", null, exception);
		}
	}

	/// <summary>
	/// FRED's message for a reject's text: shortened, with control characters such as NUL, which a VLS string cannot hold,
	/// replaced by spaces, and with anything shaped like an API key masked.
	/// </summary>
	internal static string Quote(string? message)
	{
		if (string.IsNullOrWhiteSpace(message))
			return "no message.";
		var masked = ApiKeyPattern().Replace(ControlCharacterPattern().Replace(message, " ").Trim(), "***");
		return masked.Length <= MaxQuotedMessageLength ? masked : string.Concat(masked.AsSpan(0, MaxQuotedMessageLength), "...");
	}

	static (SeriesInfoId SeriesId, DataValueTransformation Transformation) ParseCode(string code)
	{
		ArgumentNullException.ThrowIfNull(code);
		if (!Store.TryParseSymbolCode(code, out var seriesId, out var transformation))
			throw new SeriesSourceException(SeriesRequestRejectReason.UnknownSymbol, $"Unknown FRED symbol: {code}. Expected a series ID, optionally with a units suffix such as .pch.");
		return (seriesId, transformation);
	}

	static SeriesSourceException UnknownSeries(SeriesInfoId seriesId, Exception? innerException = null) =>
		new(SeriesRequestRejectReason.UnknownSymbol, $"FRED has no series {seriesId}.", null, innerException);

	static DayOfWeek? ParseWeekEndDay(string frequency)
	{
		var match = WeekEndDayPattern().Match(frequency);
		return match.Success ? Enum.Parse<DayOfWeek>(match.Groups[1].Value, ignoreCase: true) : null;
	}

	[GeneratedRegex(@"\b(?:Ending|As of)\s+(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex WeekEndDayPattern();

	// FRED API keys are 32 lowercase alphanumeric characters; mask any such run, whatever its case.
	[GeneratedRegex(@"\b[0-9A-Za-z]{32}\b", RegexOptions.CultureInvariant)]
	private static partial Regex ApiKeyPattern();

	[GeneratedRegex(@"\p{Cc}", RegexOptions.CultureInvariant)]
	private static partial Regex ControlCharacterPattern();
}
