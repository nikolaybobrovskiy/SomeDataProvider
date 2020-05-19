namespace SomeDataProvider.DataStorage.InMem
{
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

		// stb-infl.m.Russia
		// stb-infl.y.Russia
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