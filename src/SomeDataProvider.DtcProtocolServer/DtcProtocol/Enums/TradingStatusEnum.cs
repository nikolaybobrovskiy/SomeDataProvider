namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums
{
	enum TradingStatusEnum : sbyte
	{
		TradingStatusUnknown = 0,
		TradingStatusPreOpen = 1,
		TradingStatusOpen = 2,
		TradingStatusClose = 3,
		TradingStatusTradingHalt = 4
	}
}