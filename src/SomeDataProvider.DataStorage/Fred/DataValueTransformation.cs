namespace SomeDataProvider.DataStorage.Fred
{
	using System.ComponentModel;

	using NBLib.Enum;

	public enum DataValueTransformation
	{
		NoTransformation,

		[Description("Chg")]
		[StringValue("chg")]
		Change,

		[Description("Chg y/y")]
		[StringValue("ch1")]
		ChangeFromYearAgo,

		[Description("% Chg")]
		[StringValue("pch")]
		PercentChange,

		[Description("% Chg y/y")]
		[StringValue("pc1")]
		PercentChangeFromYearAgo,

		[Description("Compounded Annual Rate of Change")]
		[StringValue("pca")]
		CompoundedAnnualRateOfChange,

		[Description("Continuously Compounded Rate of Change")]
		[StringValue("cch")]
		ContinuouslyCompoundedRateOfChange,

		[Description("Continuously Compounded Annual Rate of Change")]
		[StringValue("cca")]
		ContinuouslyCompoundedAnnualRateOfChange,

		[Description("Natural Log")]
		[StringValue("log")]
		NaturalLog,
	}
}