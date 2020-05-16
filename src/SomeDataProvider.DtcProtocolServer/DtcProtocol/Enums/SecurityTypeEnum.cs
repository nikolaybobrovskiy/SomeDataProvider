namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums
{
	enum SecurityTypeEnum
	{
		SecurityTypeUnset = 0,
		SecurityTypeFutures = 1,
		SecurityTypeStock = 2,
		SecurityTypeForex = 3, // CryptoCurrencies also go into this category
		SecurityTypeIndex = 4,
		SecurityTypeFuturesStrategy = 5,
		SecurityTypeFuturesOption = 7,
		SecurityTypeStockOption = 6,
		SecurityTypeIndexOption = 8,
		SecurityTypeBond = 9,
		SecurityTypeMutualFund = 10
	}
}