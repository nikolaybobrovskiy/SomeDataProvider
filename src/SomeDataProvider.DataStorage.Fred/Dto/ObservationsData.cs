namespace SomeDataProvider.DataStorage.Fred.Dto
{
	using System;
	using System.Globalization;

	using Newtonsoft.Json;
	using Newtonsoft.Json.Converters;

	public sealed class ObservationsData
	{
		public ObservationsData(Observation[] observations)
		{
			Observations = observations;
		}

		public Observation[] Observations { get; }

		public sealed class Observation
		{
			public Observation(DateTime date, double? value)
			{
				Date = date;
				Value = value;
			}

			// "date": "1993-03-01",
			[JsonConverter(typeof(DateConverter))]
			public DateTime Date { get; }

			// "value": "."
			// "value": "674.48638"
			[JsonConverter(typeof(ValueConverter))]
			public double? Value { get; }
		}

		class ValueConverter : JsonConverter
		{
			public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
			{
				throw new NotSupportedException();
			}

			public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
			{
				if (reader.Value == null) return default(double?);

				var v = reader.Value.ToString();
				if (v == ".") return default(double?);
				if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
					return (double?)result;

				return default(double?);
			}

			public override bool CanConvert(Type objectType)
			{
				return objectType == typeof(double?);
			}
		}

		class DateConverter : DateTimeConverterBase
		{
			public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
			{
				throw new NotSupportedException();
			}

			public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
			{
				if (reader.Value == null) return default(DateTime);

				var v = reader.Value.ToString();
				if (DateTime.TryParseExact(v, Service.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result))
					return result.ToUniversalTime();

				return default(DateTime);
			}
		}
	}
}