namespace SomeDataProvider.DataStorage.Fred
{
	using System;

	using Newtonsoft.Json;

	public sealed class SeriesInfo : IEquatable<SeriesInfo>
	{
		public SeriesInfo(string id, string title, string frequencyShort, string unitsShort, string seasonalAdjustmentShort)
		{
			Id = id;
			Title = title;
			FrequencyShort = frequencyShort;
			UnitsShort = unitsShort;
			SeasonalAdjustmentShort = seasonalAdjustmentShort;
		}

		public string Id { get; }

		public string Title { get; }

		[JsonProperty("frequency_short")]
		public string FrequencyShort { get; }

		[JsonProperty("units_short")]
		public string UnitsShort { get; }

		[JsonProperty("seasonal_adjustment_short")]
		public string SeasonalAdjustmentShort { get; }

		public static bool operator ==(SeriesInfo? left, SeriesInfo? right)
		{
			return Equals(left, right);
		}

		public static bool operator !=(SeriesInfo? left, SeriesInfo? right)
		{
			return !Equals(left, right);
		}

		public bool Equals(SeriesInfo? other)
		{
			if (ReferenceEquals(null, other))
				return false;
			if (ReferenceEquals(this, other))
				return true;
			return Id == other.Id;
		}

		public override bool Equals(object? obj)
		{
			if (ReferenceEquals(null, obj))
				return false;
			if (ReferenceEquals(this, obj))
				return true;
			if (obj.GetType() != GetType())
				return false;
			return Equals((SeriesInfo)obj);
		}

		public override int GetHashCode()
		{
			return Id.GetHashCode();
		}
	}
}