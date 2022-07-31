namespace SomeDataProvider.DataStorage.Fred.Dto
{
	using System;

	using Newtonsoft.Json;

	public class SeriesInfo : IEquatable<SeriesInfo>
	{
		public SeriesInfo(SeriesInfo si)
			: this(si.Id, si.Title, si.FrequencyShort, si.UnitsShort, si.SeasonalAdjustmentShort, si.Popularity)
		{
		}

		[JsonConstructor]
		public SeriesInfo(string id, string title, string frequencyShort, string unitsShort, string seasonalAdjustmentShort, int popularity)
		{
			Id = id;
			Title = title;
			FrequencyShort = frequencyShort;
			UnitsShort = unitsShort;
			SeasonalAdjustmentShort = seasonalAdjustmentShort;
			Popularity = popularity;
			IsDiscontinued = title.Contains("DISCONTINUED");
		}

		public string Id { get; }

		public string Title { get; }

		[JsonProperty("frequency_short")]
		public string FrequencyShort { get; }

		[JsonProperty("units_short")]
		public string UnitsShort { get; }

		[JsonProperty("seasonal_adjustment_short")]
		public string SeasonalAdjustmentShort { get; }

		public int Popularity { get; }

		public bool IsDiscontinued { get; }

		public static bool operator ==(SeriesInfo? left, SeriesInfo? right)
		{
			return Equals(left, right);
		}

		public static bool operator !=(SeriesInfo? left, SeriesInfo? right)
		{
			return !Equals(left, right);
		}

		public override bool Equals(object? obj)
		{
			return ReferenceEquals(this, obj) || obj is SeriesInfo other && Equals(other);
		}

		public override int GetHashCode()
		{
			return Id.GetHashCode();
		}

		public bool Equals(SeriesInfo? other)
		{
			if (ReferenceEquals(null, other))
				return false;
			if (ReferenceEquals(this, other))
				return true;
			return Id == other.Id;
		}
	}
}