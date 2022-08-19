// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.InMem
{
	using System;

	using NBLib.Enum;

	public static class DataSources
	{
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
	}
}