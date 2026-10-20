// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Fred.Dto
{
	using System;
	using System.Globalization;

	using Newtonsoft.Json;

	/// <summary>
	/// One page of fred/series/observations with output type 1, observations by real-time period: each element is one value
	/// of one observation during one real-time period.
	/// </summary>
	public sealed class RealtimeObservationsPage : FredResponse
	{
		/// <summary>How many observations the whole answer has, over all pages; null when FRED leaves it out.</summary>
		[JsonProperty("count")]
		public int? Count { get; init; }

		[JsonProperty("observations")]
		public RealtimeObservation[]? Observations { get; init; }
	}

	/// <summary>One value of one observation during one real-time period, both ends inclusive.</summary>
	/// <remarks>Reading is strict: a date that is not <c>yyyy-MM-dd</c>, or a value that is neither a number nor <c>.</c>, fails the page.</remarks>
	public sealed class RealtimeObservation
	{
		/// <summary>The first day the value was current: its release date, or the requested real-time start when FRED clipped it.</summary>
		[JsonProperty("realtime_start", Required = Required.Always)]
		[JsonConverter(typeof(StrictDateConverter))]
		public DateOnly RealtimeStart { get; init; }

		/// <summary>The last day the value was current; 9999-12-31 while it still is.</summary>
		[JsonProperty("realtime_end", Required = Required.Always)]
		[JsonConverter(typeof(StrictDateConverter))]
		public DateOnly RealtimeEnd { get; init; }

		/// <summary>The observation's date.</summary>
		[JsonProperty("date", Required = Required.Always)]
		[JsonConverter(typeof(StrictDateConverter))]
		public DateOnly Date { get; init; }

		/// <summary>The value; null for FRED's missing value, <c>.</c>.</summary>
		[JsonProperty("value", Required = Required.Always)]
		[JsonConverter(typeof(StrictValueConverter))]
		public double? Value { get; init; }

		sealed class StrictDateConverter : JsonConverter<DateOnly>
		{
			public override DateOnly ReadJson(JsonReader reader, Type objectType, DateOnly existingValue, bool hasExistingValue, JsonSerializer serializer)
			{
				var text = reader.Value as string;
				if (DateOnly.TryParseExact(text, Service.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
					return date;
				throw new JsonSerializationException($"Expected a {Service.DateFormat} date at {reader.Path}, not '{text ?? reader.TokenType.ToString()}'.");
			}

			public override void WriteJson(JsonWriter writer, DateOnly value, JsonSerializer serializer)
			{
				throw new NotSupportedException();
			}
		}

		sealed class StrictValueConverter : JsonConverter<double?>
		{
			public override double? ReadJson(JsonReader reader, Type objectType, double? existingValue, bool hasExistingValue, JsonSerializer serializer)
			{
				var text = reader.Value as string;
				if (text == ".")
					return null;
				if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
					return value;
				throw new JsonSerializationException($"Expected a number or '.' at {reader.Path}, not '{text ?? reader.TokenType.ToString()}'.");
			}

			public override void WriteJson(JsonWriter writer, double? value, JsonSerializer serializer)
			{
				throw new NotSupportedException();
			}
		}
	}
}
