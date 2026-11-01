namespace SomeDataProvider.DtcProtocolServer.Test;

using Dtc.Protocol;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DtcProtocolServer.DtcProtocol;
using BinaryEncoder = SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary.MessageEncoder;
using VlsEncoder = SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls.MessageEncoder;

[TestFixture]
public class TInvestMarketEncodingTests
{
	[TestCase(TInvestTradingStatus.NormalTrading, TradingStatusEnum.TRADING_STATUS_OPEN)]
	[TestCase(TInvestTradingStatus.SessionOpen, TradingStatusEnum.TRADING_STATUS_OPEN)]
	[TestCase(TInvestTradingStatus.DealerNormalTrading, TradingStatusEnum.TRADING_STATUS_UNKNOWN)]
	[TestCase(TInvestTradingStatus.OpeningPeriod, TradingStatusEnum.TRADING_STATUS_PRE_OPEN)]
	[TestCase(TInvestTradingStatus.OpeningAuctionPeriod, TradingStatusEnum.TRADING_STATUS_PRE_OPEN)]
	[TestCase(TInvestTradingStatus.SessionAssigned, TradingStatusEnum.TRADING_STATUS_PRE_OPEN)]
	[TestCase(TInvestTradingStatus.BreakInTrading, TradingStatusEnum.TRADING_STATUS_TRADING_HALT)]
	[TestCase(TInvestTradingStatus.DiscreteAuction, TradingStatusEnum.TRADING_STATUS_UNKNOWN)]
	[TestCase(TInvestTradingStatus.DealerBreakInTrading, TradingStatusEnum.TRADING_STATUS_UNKNOWN)]
	[TestCase(TInvestTradingStatus.NotAvailableForTrading, TradingStatusEnum.TRADING_STATUS_CLOSE)]
	[TestCase(TInvestTradingStatus.ClosingPeriod, TradingStatusEnum.TRADING_STATUS_CLOSE)]
	[TestCase(TInvestTradingStatus.ClosingAuction, TradingStatusEnum.TRADING_STATUS_UNKNOWN)]
	[TestCase(TInvestTradingStatus.SessionClose, TradingStatusEnum.TRADING_STATUS_CLOSE)]
	[TestCase(TInvestTradingStatus.DealerNotAvailableForTrading, TradingStatusEnum.TRADING_STATUS_UNKNOWN)]
	[TestCase(TInvestTradingStatus.Unspecified, TradingStatusEnum.TRADING_STATUS_UNKNOWN)]
	[TestCase(TInvestTradingStatus.DarkPoolAuction, TradingStatusEnum.TRADING_STATUS_UNKNOWN)]
	[TestCase(TInvestTradingStatus.TradingAtClosingAuctionPrice, TradingStatusEnum.TRADING_STATUS_UNKNOWN)]
	[TestCase(TInvestTradingStatus.StabilizationAuction, TradingStatusEnum.TRADING_STATUS_UNKNOWN)]
	[TestCase((TInvestTradingStatus)999, TradingStatusEnum.TRADING_STATUS_UNKNOWN)]
	public void CompleteTradingStatusTable(TInvestTradingStatus source, TradingStatusEnum expected) => Assert.That(BinaryEncoder.MapTradingStatus(source), Is.EqualTo(expected));
	[TestCase(false, TInvestDirection.Buy, AtBidOrAskEnum8.AT_ASK_8)]
	[TestCase(false, TInvestDirection.Sell, AtBidOrAskEnum8.AT_BID_8)]
	[TestCase(false, TInvestDirection.Unknown, AtBidOrAskEnum8.BID_ASK_UNSET_8)]
	[TestCase(true, TInvestDirection.Buy, AtBidOrAskEnum8.AT_ASK_8)]
	[TestCase(true, TInvestDirection.Sell, AtBidOrAskEnum8.AT_BID_8)]
	[TestCase(true, TInvestDirection.Unknown, AtBidOrAskEnum8.BID_ASK_UNSET_8)]
	public void BothEncodingsUseV2MicrosecondsUnitsAndDirection(bool vls, TInvestDirection direction, AtBidOrAskEnum8 expected)
	{
		IMessageEncoder encoder = vls ? new VlsEncoder() : new BinaryEncoder();
		encoder.EncodeTInvestUpdate(7, new TInvestTrade("invented-uid", new(100, 123456789), 12.5, 3, direction, true), 10);
		DtcBinaryDecoder.Decode(encoder.GetEncodedMessage(), out s_MarketDataUpdateTradeV2 trade);
		Assert.That(trade.DateTime, Is.EqualTo(100_123_456L));
		Assert.That(trade.Volume, Is.EqualTo(30));
		Assert.That(trade.AtBidOrAsk, Is.EqualTo(expected));
		Assert.That(trade.SymbolID, Is.EqualTo(7));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void EmptyQuoteSidesAreUnsetInBothEncodings(bool vls)
	{
		IMessageEncoder encoder = vls ? new VlsEncoder() : new BinaryEncoder();
		encoder.EncodeTInvestUpdate(7, new TInvestQuote("invented-uid", new(100, 123456789), null, null, null, null), 10);
		DtcBinaryDecoder.Decode(encoder.GetEncodedMessage(), out s_MarketDataUpdateBidAskV2 quote);
		Assert.That(quote.BidPrice, Is.EqualTo(double.MaxValue));
		Assert.That(quote.AskPrice, Is.EqualTo(double.MaxValue));
		Assert.That(quote.BidQuantity, Is.Zero);
		Assert.That(quote.AskQuantity, Is.Zero);
		Assert.That(quote.DateTime, Is.EqualTo(100_123_456L));
	}
}
