namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary;

using System;
using Dtc.Protocol;
using SomeDataProvider.DataStorage.TInvest;

partial class MessageEncoder
{
	public void EncodeTInvestSnapshot(uint symbolId, TInvestMarketSnapshot snapshot, int lot)
	{
		var quote = snapshot.Quote;
		Append(new s_MarketDataSnapshot
		{
			SymbolID = symbolId,
			LastTradePrice = snapshot.LastPrice ?? double.MaxValue,
			LastTradeVolume = snapshot.LastLots.HasValue ? (double)snapshot.LastLots.Value * lot : double.MaxValue,
			LastTradeDateTime = snapshot.LastTime.HasValue ? snapshot.LastTime.Value.Microseconds / 1_000_000d : 0,
			BidPrice = quote?.Bid ?? double.MaxValue,
			AskPrice = quote?.Ask ?? double.MaxValue,
			BidQuantity = quote?.BidLots is long bid ? (double)bid * lot : 0,
			AskQuantity = quote?.AskLots is long ask ? (double)ask * lot : 0,
			BidAskDateTime = quote?.Time.Microseconds / 1_000_000d ?? 0,
			TradingStatus = MapTradingStatus(snapshot.Status),
		});
	}

	public void EncodeTInvestUpdate(uint symbolId, TInvestMarketEvent update, int lot)
	{
		switch (update)
		{
			case TInvestTrade trade:
				Append(new s_MarketDataUpdateTradeV2
				{
					SymbolID = symbolId,
					Price = trade.Price,
					Volume = (double)trade.Lots * lot,
					DateTime = trade.Time.Microseconds,
					AtBidOrAsk = trade.Direction switch
					{
						TInvestDirection.Buy => AtBidOrAskEnum8.AT_ASK_8,
						TInvestDirection.Sell => AtBidOrAskEnum8.AT_BID_8,
						_ => AtBidOrAskEnum8.BID_ASK_UNSET_8
					},
				});
				break;
			case TInvestQuote quote:
				Append(new s_MarketDataUpdateBidAskV2
				{
					SymbolID = symbolId,
					BidPrice = quote.Bid ?? double.MaxValue,
					AskPrice = quote.Ask ?? double.MaxValue,
					BidQuantity = quote.BidLots.HasValue ? (double)quote.BidLots.Value * lot : 0,
					AskQuantity = quote.AskLots.HasValue ? (double)quote.AskLots.Value * lot : 0,
					DateTime = quote.Time.Microseconds,
				});
				break;
			case TInvestStatus status:
				Append(new s_TradingSymbolStatus
				{
					SymbolID = symbolId,
					Status = MapTradingStatus(status.Status)
				});
				break;
			default:
				throw new ArgumentException("This event is not a market update.", nameof(update));
		}
	}

	public void EncodeMarketDepthSnapshotLevel(uint symbolId, AtBidOrAskEnum8 side, float price, float quantity, ushort level, FinalUpdateInBatchEnum finalUpdateInBatch) => Append(new s_MarketDepthSnapshotLevelFloat
	{
		SymbolID = symbolId,
		Side = side,
		Price = price,
		Quantity = quantity,
		NumOrders = 0,
		Level = level,
		FinalUpdateInBatch = finalUpdateInBatch,
	});

	public void EncodeMarketDepthUpdateLevel(uint symbolId, long dateTimeMilliseconds, AtBidOrAskEnum8 side, MarketDepthUpdateTypeEnum updateType, double price, double quantity, ushort level, FinalUpdateInBatchEnum finalUpdateInBatch) => Append(new s_MarketDepthUpdateLevelV2
	{
		SymbolID = symbolId,
		DateTime = dateTimeMilliseconds,
		Price = price,
		Quantity = quantity,
		NumOrders = 0,
		Level = level,
		Side = side,
		UpdateType = updateType,
		FinalUpdateInBatch = finalUpdateInBatch,
	});

	public void EncodeFeedStatus(uint symbolId, bool available) => Append(new s_MarketDataFeedSymbolStatus
	{
		SymbolID = symbolId,
		Status = available ? MarketDataFeedStatusEnum.MARKET_DATA_FEED_AVAILABLE : MarketDataFeedStatusEnum.MARKET_DATA_FEED_UNAVAILABLE,
	});
	internal static TradingStatusEnum MapTradingStatus(TInvestTradingStatus status) => status switch
	{
		TInvestTradingStatus.NormalTrading or TInvestTradingStatus.SessionOpen => TradingStatusEnum.TRADING_STATUS_OPEN,
		TInvestTradingStatus.OpeningPeriod or TInvestTradingStatus.OpeningAuctionPeriod or TInvestTradingStatus.SessionAssigned => TradingStatusEnum.TRADING_STATUS_PRE_OPEN,
		TInvestTradingStatus.BreakInTrading => TradingStatusEnum.TRADING_STATUS_TRADING_HALT,
		TInvestTradingStatus.NotAvailableForTrading or TInvestTradingStatus.ClosingPeriod or TInvestTradingStatus.SessionClose => TradingStatusEnum.TRADING_STATUS_CLOSE,
		TInvestTradingStatus.Unspecified or TInvestTradingStatus.DarkPoolAuction or TInvestTradingStatus.TradingAtClosingAuctionPrice or TInvestTradingStatus.StabilizationAuction => TradingStatusEnum.TRADING_STATUS_UNKNOWN,
		_ => TradingStatusEnum.TRADING_STATUS_UNKNOWN,
	};
}
