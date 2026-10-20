// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Fred.Dto
{
	using Newtonsoft.Json;

	/// <summary>
	/// A series' metadata from fred/series, as FRED's own strings, such as "Weekly, Ending Friday", "SAAR" or
	/// "2013-07-31 09:26:16-05". Every field is optional; a missing one is null.
	/// </summary>
	public sealed class SeriesDetails
	{
		[JsonProperty("id")]
		public string? Id { get; init; }

		[JsonProperty("title")]
		public string? Title { get; init; }

		/// <summary>The first observation date, <c>yyyy-MM-dd</c>.</summary>
		[JsonProperty("observation_start")]
		public string? ObservationStart { get; init; }

		/// <summary>The last observation date, <c>yyyy-MM-dd</c>.</summary>
		[JsonProperty("observation_end")]
		public string? ObservationEnd { get; init; }

		/// <summary>The native frequency, such as "Quarterly" or "Weekly, Ending Friday".</summary>
		[JsonProperty("frequency")]
		public string? Frequency { get; init; }

		/// <summary>The native frequency's code: D, W, BW, M, Q, SA or A.</summary>
		[JsonProperty("frequency_short")]
		public string? FrequencyShort { get; init; }

		[JsonProperty("units")]
		public string? Units { get; init; }

		[JsonProperty("units_short")]
		public string? UnitsShort { get; init; }

		[JsonProperty("seasonal_adjustment")]
		public string? SeasonalAdjustment { get; init; }

		/// <summary>The seasonal adjustment's code: NSA, SA, SAAR, SSA or NA.</summary>
		[JsonProperty("seasonal_adjustment_short")]
		public string? SeasonalAdjustmentShort { get; init; }

		/// <summary>The last update, <c>yyyy-MM-dd HH:mm:ss</c> and a UTC offset in hours, such as <c>2013-07-31 09:26:16-05</c>.</summary>
		[JsonProperty("last_updated")]
		public string? LastUpdated { get; init; }
	}

	/// <summary>The answer of fred/series: the series as FRED knows it today, the first element of <see cref="Seriess"/>.</summary>
	public sealed class SeriesDetailsResponse : FredResponse
	{
		[JsonProperty("seriess")]
		public SeriesDetails[]? Seriess { get; init; }
	}
}
