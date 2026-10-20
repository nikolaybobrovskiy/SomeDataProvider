// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Fred
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using System.Linq;
	using System.Net;
	using System.Runtime.CompilerServices;
	using System.Threading;
	using System.Threading.Tasks;

	using NBLib.Enum;

	using SomeDataProvider.DataStorage.Fred.Dto;

	// Series metadata and observations by real-time period, for the private series extension of the DTC server.
	public sealed partial class Store
	{
		/// <summary>
		/// Parses a FRED symbol code without the data source prefix: <c>SERIES_ID</c>, optionally followed by a units suffix
		/// such as <c>.pch</c>, as <see cref="GetSymbolAsync"/> accepts it.
		/// </summary>
		/// <returns>False, without throwing, for an empty series ID or an unknown suffix.</returns>
		public static bool TryParseSymbolCode(string code, [NotNullWhen(true)] out SeriesInfoId? seriesId, out DataValueTransformation transformation)
		{
			ArgumentNullException.ThrowIfNull(code);
			seriesId = null;
			transformation = DataValueTransformation.NoTransformation;
			var separatorIndex = code.IndexOf('.', StringComparison.Ordinal);
			var id = separatorIndex >= 0 ? code[..separatorIndex] : code;
			if (id.Length == 0 || id.Any(char.IsWhiteSpace))
				return false;
			if (separatorIndex >= 0)
			{
				var suffix = code[(separatorIndex + 1)..];
				var match = Enum.GetValues<DataValueTransformation>().Where(t =>
					t != DataValueTransformation.NoTransformation && string.Equals(suffix, t.GetStringValue(), StringComparison.Ordinal)).ToArray();
				if (match.Length != 1)
					return false;
				transformation = match[0];
			}
			seriesId = (SeriesInfoId)id;
			return true;
		}

		/// <summary>Reads a series' metadata as FRED knows it today; see <see cref="Service.GetSeriesDetailsAsync"/>.</summary>
		public Task<SeriesDetails?> GetSeriesDetailsAsync(SeriesInfoId seriesId, CancellationToken cancellationToken = default) =>
			_fredService.GetSeriesDetailsAsync(seriesId, cancellationToken);

		/// <summary>
		/// Reads a series' observations by real-time period, one FRED page at a time, in ascending observation date and then
		/// ascending real-time start.
		/// </summary>
		/// <param name="seriesId">The series.</param>
		/// <param name="transformation">The units transformation FRED applies to every value.</param>
		/// <param name="observationStart">The first observation date; null for the earliest.</param>
		/// <param name="observationEnd">The last observation date; null for the latest.</param>
		/// <param name="realtimeStart">The start of the real-time window, such as <see cref="Service.EarliestRealtimeDate"/>.</param>
		/// <param name="realtimeEnd">The end of the real-time window, inclusive, such as <see cref="Service.LatestRealtimeDate"/>.</param>
		/// <param name="pageSize">The rows FRED is asked for at once, from 1 to <see cref="Service.MaxObservationsLimit"/>.</param>
		/// <param name="cancellationToken">Cancels the reading.</param>
		/// <returns>
		/// The rows in batches. FRED is asked for the next page only when the caller asks for the next batch, so the caller can
		/// send a batch before the next page is fetched.
		/// </returns>
		/// <remarks>
		/// FRED orders an answer by observation date only, and a page can end inside the real-time periods of one observation. So
		/// the rows of a page's last observation date wait for the next page, and each batch is sorted by real-time start within
		/// each date. An answer that goes back to an observation date already returned fails with
		/// <see cref="FredApiException"/>.
		/// </remarks>
		/// <exception cref="FredApiException">FRED answered with an error, or with rows out of order.</exception>
		public async IAsyncEnumerable<IReadOnlyList<RealtimeObservation>> ReadRealtimeObservationsAsync(
			SeriesInfoId seriesId,
			DataValueTransformation transformation,
			DateOnly? observationStart,
			DateOnly? observationEnd,
			DateOnly realtimeStart,
			DateOnly realtimeEnd,
			int pageSize = Service.MaxObservationsLimit,
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
			ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, Service.MaxObservationsLimit);
			var offset = 0;
			var heldBack = new List<RealtimeObservation>();
			DateOnly? lastReturnedDate = null;
			while (true)
			{
				var page = await _fredService.GetRealtimeObservationsAsync(seriesId, transformation, observationStart, observationEnd, realtimeStart, realtimeEnd, offset, pageSize, cancellationToken);
				// A successful answer always has the array; without it, or ending before FRED's own count, it is truncated.
				var rows = page.Observations ?? throw new FredApiException(HttpStatusCode.OK, "FRED's answer has no observations.");
				offset += rows.Length;
				if (rows.Length < pageSize && page.Count is { } count && offset < count)
					throw new FredApiException(HttpStatusCode.OK, $"FRED's answer ended after {offset} of its {count} rows.");
				var hasMore = rows.Length == pageSize && (page.Count == null || offset < page.Count);
				// OrderBy is stable, so FRED's order stays for equal keys.
				var batch = heldBack.Concat(rows).OrderBy(r => r.Date).ThenBy(r => r.RealtimeStart).ToList();
				heldBack.Clear();
				if (batch.Count > 0 && lastReturnedDate is { } last && batch[0].Date <= last)
					throw new FredApiException(HttpStatusCode.OK, $"FRED returned observation {batch[0].Date:yyyy-MM-dd} after observation {last:yyyy-MM-dd} on an earlier page.");
				if (hasMore && batch.Count > 0)
				{
					var lastDate = batch[^1].Date;
					var firstOfLastDate = batch.FindIndex(r => r.Date == lastDate);
					heldBack.AddRange(batch.Skip(firstOfLastDate));
					batch.RemoveRange(firstOfLastDate, batch.Count - firstOfLastDate);
				}
				if (batch.Count > 0)
				{
					lastReturnedDate = batch[^1].Date;
					yield return batch;
				}
				if (!hasMore)
					yield break;
			}
		}
	}
}
