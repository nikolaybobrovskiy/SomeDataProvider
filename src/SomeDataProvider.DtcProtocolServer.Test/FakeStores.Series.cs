// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Collections.Generic;
	using System.Runtime.CompilerServices;
	using System.Threading;
	using System.Threading.Tasks;

	using Dtc.Protocol;
	using Dtc.Protocol.Private;

	using SomeDataProvider.DtcProtocolServer.Data;

	/// <summary>
	/// The fake stores' series extension: like the server's StoresProvider, they answer by the full symbol. They know one
	/// series, <c>fred-</c><see cref="SeriesId"/>; <see cref="FailingSeriesSymbol"/> fails as FRED being unavailable would.
	/// </summary>
	sealed partial class FakeStores : ISeriesSource
	{
		/// <summary>A symbol whose requests fail with <see cref="SeriesRequestRejectReason.SourceUnavailable"/> and a 30-second hint.</summary>
		public const string FailingSeriesSymbol = "fred-FAIL";

		/// <summary>The retry hint of a simulated failure.</summary>
		public static readonly TimeSpan FailureRetryAfter = TimeSpan.FromSeconds(30);

		/// <summary>The metadata of <c>fred-</c><see cref="SeriesId"/>, without RequestID, Symbol, and Exchange.</summary>
		public static readonly SeriesMetadataResponse SeriesMetadata = new()
		{
			Title = "Gross Domestic Product",
			Units = "Billions of Dollars",
			UnitsShort = "Bil. of $",
			Frequency = SeriesFrequency.Quarterly,
			UnitsTransformation = SeriesUnitsTransformation.Levels,
			SeasonalAdjustment = SeriesSeasonalAdjustment.SeasonallyAdjustedAnnualRate,
			ObservationStart = new DateOnly(1947, 1, 1),
			ObservationEnd = new DateOnly(2026, 4, 1),
			LastUpdated = DtcTimestamp.FromDateTimeOffset(new DateTimeOffset(2026, 9, 25, 7, 51, 2, TimeSpan.FromHours(-5))),
		};

		/// <summary>
		/// The default vintages of <c>fred-</c><see cref="SeriesId"/>, in two batches: two revisions of 2025Q4, a missing value in
		/// the first vintage of 2026Q1, and its later value, still current.
		/// </summary>
		public static readonly IReadOnlyList<IReadOnlyList<SeriesVintage>> DefaultVintageBatches = new[]
		{
			new SeriesVintage[]
			{
				new(new DateOnly(2025, 10, 1), new DateOnly(2026, 1, 29), new DateOnly(2026, 2, 26), 30_100.5),
				new(new DateOnly(2025, 10, 1), new DateOnly(2026, 2, 27), new DateOnly(9999, 12, 31), 30_120.25),
			},
			new SeriesVintage[]
			{
				new(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30), new DateOnly(2026, 5, 28), null),
				new(new DateOnly(2026, 1, 1), new DateOnly(2026, 5, 29), new DateOnly(9999, 12, 31), 30_300),
			},
		};

		int _seriesRequestCount;

		/// <summary>The batches the vintages of <c>fred-</c><see cref="SeriesId"/> have; <see cref="DefaultVintageBatches"/> by default.</summary>
		public IReadOnlyList<IReadOnlyList<SeriesVintage>> VintageBatches { get; set; } = DefaultVintageBatches;

		/// <summary>After how many batches the vintages fail with <see cref="SeriesRequestRejectReason.SourceUnavailable"/>; null for never.</summary>
		public int? VintagesFailAfterBatches { get; set; }

		/// <summary>How many metadata and vintages requests reached the source.</summary>
		public int SeriesRequestCount => Volatile.Read(ref _seriesRequestCount);

		/// <summary>The last vintages request that reached the source.</summary>
		public SeriesVintagesRequest? LastVintagesRequest { get; private set; }

		public Task<SeriesMetadataResponse> GetSeriesMetadataAsync(string symbol, CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _seriesRequestCount);
			return symbol switch
			{
				Prefix + SeriesId => Task.FromResult(SeriesMetadata),
				FailingSeriesSymbol => Task.FromException<SeriesMetadataResponse>(SimulatedFailure()),
				_ => Task.FromException<SeriesMetadataResponse>(new SeriesSourceException(SeriesRequestRejectReason.UnknownSymbol, $"Unknown symbol: {symbol}.")),
			};
		}

		public async IAsyncEnumerable<IReadOnlyList<SeriesVintage>> GetSeriesVintagesAsync(SeriesVintagesRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _seriesRequestCount);
			LastVintagesRequest = request;
			await Task.Yield();
			if (request.Symbol == FailingSeriesSymbol)
				throw SimulatedFailure();
			if (request.Symbol != Prefix + SeriesId)
				throw new SeriesSourceException(SeriesRequestRejectReason.UnknownSymbol, $"Unknown symbol: {request.Symbol}.");
			for (var i = 0; i < VintageBatches.Count; i++)
			{
				if (i == VintagesFailAfterBatches)
					throw SimulatedFailure();
				cancellationToken.ThrowIfCancellationRequested();
				yield return VintageBatches[i];
			}
			if (VintagesFailAfterBatches == VintageBatches.Count)
				throw SimulatedFailure();
		}

		static SeriesSourceException SimulatedFailure() =>
			new(SeriesRequestRejectReason.SourceUnavailable, "Simulated FRED failure.", FailureRetryAfter);
	}
}
