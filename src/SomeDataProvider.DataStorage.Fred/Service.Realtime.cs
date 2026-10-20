// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Fred
{
	using System;
	using System.Globalization;
	using System.IO;
	using System.Net;
	using System.Net.Http;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	using NBLib.Enum;
	using NBLib.Logging;

	using Newtonsoft.Json;
	using Newtonsoft.Json.Linq;

	using SomeDataProvider.DataStorage.Fred.Dto;

	// Series metadata and observations by real-time period, for the private series extension of the DTC server
	// (docs/dtc-series-extension.md in MrktPltfrm). Unlike the older calls, these check FRED's HTTP status: an error is a
	// FredApiException, never an empty result, so a caller can tell a missing series from an outage.
	public sealed partial class Service
	{
		/// <summary>The most observations FRED returns at once: series/observations' largest <c>limit</c>, 100,000.</summary>
		public const int MaxObservationsLimit = 100_000;

		/// <summary>The earliest real-time date FRED knows, its default real-time start for all history: 1776-07-04.</summary>
		public static readonly DateOnly EarliestRealtimeDate = new(1776, 7, 4);

		/// <summary>The latest real-time date, which means "still current": 9999-12-31.</summary>
		public static readonly DateOnly LatestRealtimeDate = new(9999, 12, 31);

		/// <summary>Reads a series' metadata (fred/series) as FRED knows it today.</summary>
		/// <returns>The metadata; null when FRED says that the series does not exist.</returns>
		/// <exception cref="FredApiException">
		/// FRED answered with another error, or with a body that cannot be read or that holds no series: FRED reports a
		/// missing series as an error, so a successful answer without one is unusable, not a missing series.
		/// </exception>
		/// <exception cref="HttpRequestException">FRED could not be reached.</exception>
		public async Task<SeriesDetails?> GetSeriesDetailsAsync(SeriesInfoId seriesId, CancellationToken cancellationToken = default)
		{
			return await _l.LogOperationAsync(async () =>
			{
				try
				{
					var response = await GetFredAsync<SeriesDetailsResponse>($"series?series_id={Escape(seriesId)}&api_key={_apiKey}&file_type=json", cancellationToken);
					return response.Seriess is { Length: > 0 } seriess && seriess[0] is { } details
						? details
						: throw new FredApiException(HttpStatusCode.OK, "FRED's answer holds no series.");
				}
				catch (FredApiException ex) when (ex.IsSeriesNotFound)
				{
					return null;
				}
			}, "GetSeriesDetails({seriesId})", seriesId);
		}

		/// <summary>
		/// Reads one page of a series' observations by real-time period (fred/series/observations, output type 1), in FRED's
		/// order: ascending observation date.
		/// </summary>
		/// <param name="seriesId">The series.</param>
		/// <param name="transformation">The units transformation FRED applies to every value.</param>
		/// <param name="observationStart">The first observation date; null for FRED's default, the earliest.</param>
		/// <param name="observationEnd">The last observation date; null for FRED's default, the latest.</param>
		/// <param name="realtimeStart">The start of the real-time window; FRED's default is today, so it is always sent.</param>
		/// <param name="realtimeEnd">The end of the real-time window, inclusive; always sent, as <paramref name="realtimeStart"/>.</param>
		/// <param name="offset">How many rows of the answer to skip.</param>
		/// <param name="limit">The most rows to return, from 1 to <see cref="MaxObservationsLimit"/>.</param>
		/// <param name="cancellationToken">Cancels the request.</param>
		/// <exception cref="FredApiException">FRED answered with an error, or with a body that cannot be read.</exception>
		/// <exception cref="HttpRequestException">FRED could not be reached.</exception>
		public async Task<RealtimeObservationsPage> GetRealtimeObservationsAsync(
			SeriesInfoId seriesId,
			DataValueTransformation transformation,
			DateOnly? observationStart,
			DateOnly? observationEnd,
			DateOnly realtimeStart,
			DateOnly realtimeEnd,
			int offset,
			int limit,
			CancellationToken cancellationToken = default)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(offset);
			ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
			ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxObservationsLimit);
			return await _l.LogOperationAsync(async () =>
			{
				var uri = new StringBuilder("series/observations?series_id=").Append(Escape(seriesId))
					.Append("&api_key=").Append(_apiKey)
					.Append("&file_type=json&output_type=1&sort_order=asc")
					.Append("&realtime_start=").Append(FormatDate(realtimeStart))
					.Append("&realtime_end=").Append(FormatDate(realtimeEnd));
				if (observationStart is { } start)
					uri.Append("&observation_start=").Append(FormatDate(start));
				if (observationEnd is { } end)
					uri.Append("&observation_end=").Append(FormatDate(end));
				if (transformation != DataValueTransformation.NoTransformation)
					uri.Append("&units=").Append(transformation.GetStringValue());
				uri.Append("&offset=").Append(offset.ToString(CultureInfo.InvariantCulture))
					.Append("&limit=").Append(limit.ToString(CultureInfo.InvariantCulture));
				return await GetFredAsync<RealtimeObservationsPage>(uri.ToString(), cancellationToken);
			}, "GetRealtimeObservations({seriesId},{transformation},{observationStart},{observationEnd},{realtimeStart},{realtimeEnd},{offset},{limit})", seriesId, transformation, observationStart, observationEnd, realtimeStart, realtimeEnd, offset, limit);
		}

		static string Escape(SeriesInfoId seriesId) => Uri.EscapeDataString(seriesId.ToString());

		static string FormatDate(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

		/// <summary>FRED's <c>error_message</c> from an error body; null when the body has none or is not JSON.</summary>
		static string? ReadErrorMessage(JsonReader reader)
		{
			try
			{
				return JToken.ReadFrom(reader) is JObject body ? body["error_message"]?.ToString() : null;
			}
			catch (JsonException)
			{
				return null;
			}
		}

		static TimeSpan? GetRetryAfter(HttpResponseMessage response)
		{
			var retryAfter = response.Headers.RetryAfter;
			if (retryAfter?.Delta is { } delta)
				return delta;
			if (retryAfter?.Date is { } date)
				return date - DateTimeOffset.UtcNow;
			return null;
		}

		/// <summary>Sends a GET request and reads FRED's JSON answer as <typeparamref name="T"/>.</summary>
		/// <exception cref="FredApiException">
		/// The HTTP status is not a success, the body reports an error, or the body cannot be read as <typeparamref name="T"/>.
		/// </exception>
		async Task<T> GetFredAsync<T>(string uri, CancellationToken cancellationToken)
			where T : FredResponse
		{
			using var requestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
			using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
			var retryAfter = GetRetryAfter(response);
			await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			using var textReader = new StreamReader(stream, Encoding.UTF8);
			// Dates stay strings, so the DTOs' converters read them exactly as FRED wrote them.
			using var jsonReader = new JsonTextReader(textReader) { DateParseHandling = DateParseHandling.None };
			if (!response.IsSuccessStatusCode)
				throw new FredApiException(response.StatusCode, Redact(ReadErrorMessage(jsonReader) ?? response.ReasonPhrase), retryAfter);
			T? result;
			try
			{
				result = JsonSerializer.CreateDefault().Deserialize<T>(jsonReader);
			}
			catch (JsonException ex)
			{
				// Without ex as the inner exception: its message quotes FRED's text unmasked, and a log writes the whole chain.
				throw new FredApiException(response.StatusCode, $"FRED's answer cannot be read: {Redact(ex.Message)}", retryAfter);
			}
			if (result == null)
				throw new FredApiException(response.StatusCode, "FRED's answer is empty.", retryAfter);
			if (result.ErrorMessage != null)
				throw new FredApiException(response.StatusCode, Redact(result.ErrorMessage), retryAfter);
			return result;
		}

		/// <summary>Masks the provider's API key in text FRED wrote, in case it ever echoes the request, before it reaches an exception or a log.</summary>
		string? Redact(string? text)
		{
			var key = _apiKey.ToString();
			return string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key) ? text : text.Replace(key, "***", StringComparison.Ordinal);
		}
	}
}
