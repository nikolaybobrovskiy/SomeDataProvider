namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums
{
	enum HistoricalPriceDataRejectReasonCodeEnum : short
	{
		HpdrUnset = 0,
		HpdrUnableToServeDataRetryInSpecifiedSeconds = 1,
		HpdrUnableToServeDataDoNotRetry = 2,
		HpdrDataRequestOutsideBoundsOfAvailableData = 3,
		HpdrGeneralRejectError = 4
	}
}