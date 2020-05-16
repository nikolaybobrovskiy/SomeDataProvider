namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums
{
	enum PriceDisplayFormatEnum
	{
		PriceDisplayFormatUnset = -1,

		//The following formats indicate the number of decimal places to be displayed
		PriceDisplayFormatDecimal0 = 0,
		PriceDisplayFormatDecimal1 = 1,
		PriceDisplayFormatDecimal2 = 2,
		PriceDisplayFormatDecimal3 = 3,
		PriceDisplayFormatDecimal4 = 4,
		PriceDisplayFormatDecimal5 = 5,
		PriceDisplayFormatDecimal6 = 6,
		PriceDisplayFormatDecimal7 = 7,
		PriceDisplayFormatDecimal8 = 8,
		PriceDisplayFormatDecimal9 = 9,

		//The following formats are fractional formats
		PriceDisplayFormatDenominator256 = 356,
		PriceDisplayFormatDenominator128 = 228,
		PriceDisplayFormatDenominator64 = 164,
		PriceDisplayFormatDenominator32Eighths = 140,
		PriceDisplayFormatDenominator32Quarters = 136,
		PriceDisplayFormatDenominator32Halves = 134,
		PriceDisplayFormatDenominator32 = 132,
		PriceDisplayFormatDenominator16 = 116,
		PriceDisplayFormatDenominator8 = 108,
		PriceDisplayFormatDenominator4 = 104,
		PriceDisplayFormatDenominator2 = 102
	}
}