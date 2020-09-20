// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.InMem
{
	using System;

	using NBLib.Enum;

	public static class DataSources
	{
		public const string Fred = "fred";

		public const string CentralBankOfRussia = "cbr";

		public const string StatBureau = "stb";
		public const string StatBureauInflationPrefix = "infl";

		public static readonly int StatBureauInflationPeriodIndex = StatBureau.Length + 1 + StatBureauInflationPrefix.Length + 1;

		public static readonly string[] StatBureauCountries = new[]
		{
			"belarus",
			"brazil",
			"canada",
			"european-union",
			"eurozone",
			"france",
			"germany",
			"greece",
			"india",
			"japan",
			"kazakhstan",
			"mexico",
			"russia",
			"spain",
			"turkey",
			"ukraine",
			"united-kingdom",
			"united-states",
		};

		public static string GetStatBureauInflationCountry(this string code)
		{
			return code.Substring(DataSources.StatBureauInflationPeriodIndex + 2);
		}

		public static char GetStatBureauInflationPeriodicity(this string code)
		{
			return code[DataSources.StatBureauInflationPeriodIndex];
		}

		public static FredSymbol GetFredSymbol(this string code)
		{
			var normalizedCode = code.AsSpan(Fred.Length + 1);
			var units = DataStorage.Fred.DataValueTransformation.NoTransformation;
			string seriesId;
			var optionsSeparatorIndex = normalizedCode.IndexOf('.');
			if (optionsSeparatorIndex >= 0)
			{
				units = normalizedCode.Slice(optionsSeparatorIndex + 1).ToEnumAsStringValue<Fred.DataValueTransformation>();
				seriesId = normalizedCode.Slice(0, optionsSeparatorIndex).ToString();
			}
			else
			{
				seriesId = normalizedCode.ToString();
			}
			return new FredSymbol(seriesId, units);
		}

		public sealed class FredSymbol : IEquatable<FredSymbol>
		{
			internal FredSymbol(string seriesId, Fred.DataValueTransformation units = DataStorage.Fred.DataValueTransformation.NoTransformation)
			{
				SeriesId = seriesId;
				Units = units;
			}

			public string SeriesId { get; }

			public Fred.DataValueTransformation Units { get; }

			public static bool operator ==(FredSymbol? left, FredSymbol? right)
			{
				return Equals(left, right);
			}

			public static bool operator !=(FredSymbol? left, FredSymbol? right)
			{
				return !Equals(left, right);
			}

			public bool Equals(FredSymbol? other)
			{
				if (ReferenceEquals(null, other))
					return false;
				if (ReferenceEquals(this, other))
					return true;
				return SeriesId == other.SeriesId && Units == other.Units;
			}

			public override bool Equals(object? obj)
			{
				return ReferenceEquals(this, obj) || obj is FredSymbol other && Equals(other);
			}

			public override int GetHashCode()
			{
				return HashCode.Combine(SeriesId, (int)Units);
			}
		}
	}
}