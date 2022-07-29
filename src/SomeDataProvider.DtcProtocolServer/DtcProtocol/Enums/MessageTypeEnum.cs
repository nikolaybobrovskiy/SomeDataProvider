// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums
{
	// https://www.sierrachart.com/index.php?page=doc/DTCMessages_All.php
	enum MessageTypeEnum : ushort
	{
		LogonRequest = 1,
		LogonResponse = 2,
		Heartbeat = 3,
		Logoff = 5,
		EncodingRequest = 6,
		EncodingResponse = 7,

		MarketDataRequest = 101,
		MarketDataReject = 103,
		MarketDataSnapshot = 104,
		MarketDataSnapshotInt = 125,

		MarketDataUpdateTrade = 107,
		MarketDataUpdateTradeCompact = 112,
		MarketDataUpdateTradeInt = 126,
		MarketDataUpdateLastTradeSnapshot = 134,
		MarketDataUpdateTradeWithUnbundledIndicator = 137,
		MarketDataUpdateTradeNoTimestamp = 142,

		MarketDataUpdateBidAsk = 108,
		MarketDataUpdateBidAskCompact = 117,
		MarketDataUpdateBidAskInt = 127,
		MarketDataUpdateBidAskNoTimestamp = 143,
		MarketDataUpdateBidAskFloatWithMilliseconds = 144,

		MarketDataUpdateSessionOpen = 120,
		MarketDataUpdateSessionOpenInt = 128,
		MarketDataUpdateSessionHigh = 114,
		MarketDataUpdateSessionHighInt = 129,
		MarketDataUpdateSessionLow = 115,
		MarketDataUpdateSessionLowInt = 130,
		MarketDataUpdateSessionVolume = 113,
		MarketDataUpdateOpenInterest = 124,
		MarketDataUpdateSessionSettlement = 119,
		MarketDataUpdateSessionSettlementInt = 131,
		MarketDataUpdateSessionNumTrades = 135,
		MarketDataUpdateTradingSessionDate = 136,

		MarketDepthRequest = 102,
		MarketDepthReject = 121,
		MarketDepthSnapshotLevel = 122,
		MarketDepthSnapshotLevelInt = 132,
		MarketDepthSnapshotLevelFloat = 145,
		MarketDepthUpdateLevel = 106,
		MarketDepthUpdateLevelFloatWithMilliseconds = 140,
		MarketDepthUpdateLevelNoTimestamp = 141,
		MarketDepthUpdateLevelInt = 133,

		MarketDataFeedStatus = 100,
		MarketDataFeedSymbolStatus = 116,
		TradingSymbolStatus = 138,

		SubmitNewSingleOrder = 208,
		SubmitNewSingleOrderInt = 206,

		SubmitNewOcoOrder = 201,
		SubmitNewOcoOrderInt = 207,
		SubmitFlattenPositionOrder = 209,

		CancelOrder = 203,

		CancelReplaceOrder = 204,
		CancelReplaceOrderInt = 205,

		OpenOrdersRequest = 300,
		OpenOrdersReject = 302,

		OrderUpdate = 301,

		HistoricalOrderFillsRequest = 303,
		HistoricalOrderFillsReject = 308,
		HistoricalOrderFillResponse = 304,

		CurrentPositionsRequest = 305,
		CurrentPositionsReject = 307,

		PositionUpdate = 306,

		TradeAccountsRequest = 400,
		TradeAccountResponse = 401,

		ExchangeListRequest = 500,
		ExchangeListResponse = 501,

		SymbolsForExchangeRequest = 502,
		UnderlyingSymbolsForExchangeRequest = 503,
		SymbolsForUnderlyingRequest = 504,
		SecurityDefinitionForSymbolRequest = 506,
		SecurityDefinitionResponse = 507,

		SymbolSearchRequest = 508,

		SecurityDefinitionReject = 509,

		AccountBalanceRequest = 601,
		AccountBalanceReject = 602,
		AccountBalanceUpdate = 600,
		AccountBalanceAdjustment = 607,
		AccountBalanceAdjustmentReject = 608,
		AccountBalanceAdjustmentComplete = 609,
		HistoricalAccountBalancesRequest = 603,
		HistoricalAccountBalancesReject = 604,
		HistoricalAccountBalanceResponse = 606,

		UserMessage = 700,
		GeneralLogMessage = 701,
		AlertMessage = 702,

		JournalEntryAdd = 703,
		JournalEntriesRequest = 704,
		JournalEntriesReject = 705,
		JournalEntryResponse = 706,

		HistoricalPriceDataRequest = 800,
		HistoricalPriceDataResponseHeader = 801,
		HistoricalPriceDataReject = 802,
		HistoricalPriceDataRecordResponse = 803,
		HistoricalPriceDataTickRecordResponse = 804,
		HistoricalPriceDataRecordResponseInt = 805,
		HistoricalPriceDataTickRecordResponseInt = 806,
		HistoricalPriceDataResponseTrailer = 807,
	}
}