namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using Dtc.Protocol;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DtcProtocolServer.Market;
using BinaryEncoder = SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary.MessageEncoder;

public partial class SessionTests
{
	[Test]
	public void FeedStatusPurgedTradesAreReportedAsLostOnTheNextHeartbeat()
	{
		var clock = new ManualTimeProvider();
		using var connection = Connect(new FakeHistory(0), onlyHistoryServer: false, timeProvider: clock);
		Logon(connection, 10);
		var queue = new SessionMarketDataQueue(8);
		var session = connection.ServerSession;
		var state = InstallMarketState(session, queue, 1);
		lock (SessionField("_sessionLock").GetValue(session)!)
		{
			queue.Enqueue(1, TradeFrame(1, 12.5));
			queue.Enqueue(1, TradeFrame(1, 12.6));
			ReceiveMarket(session, state, 1, new HubFeedStatus(false));
		}
		Assert.That(DtcWire.TypeOf(connection.ReadFrame()), Is.EqualTo(DtcConstants.MARKET_DATA_FEED_SYMBOL_STATUS));
		Assert.That(queue.Count, Is.Zero);
		clock.Advance(TimeSpan.FromSeconds(10));
		DtcBinaryDecoder.Decode(connection.ReadFrame(), out s_Heartbeat heartbeat);
		Assert.That(heartbeat.NumDroppedMessages, Is.EqualTo(2), "Feed loss must not erase the evidence of discarded trades.");
		AssertResponsive(connection);
		clock.Advance(TimeSpan.FromSeconds(10));
		DtcBinaryDecoder.Decode(connection.ReadFrame(), out heartbeat);
		Assert.That(heartbeat.NumDroppedMessages, Is.Zero, "Each lost trade is reported once.");
	}

	[Test]
	public void TradingStatusFenceSendsQueuedTradesBeforeTheStatus()
	{
		using var connection = Connect(new FakeHistory(0), onlyHistoryServer: false);
		Logon(connection, 0);
		var queue = new SessionMarketDataQueue(8);
		var session = connection.ServerSession;
		var state = InstallMarketState(session, queue, 1);
		lock (SessionField("_sessionLock").GetValue(session)!)
		{
			queue.Enqueue(1, TradeFrame(1, 12.5));
			queue.Enqueue(2, TradeFrame(2, 13.5));
			ReceiveMarket(session, state, 1, new HubUpdate(new TInvestStatus("synthetic-live-uid", TInvestTradingStatus.BreakInTrading)));
		}
		Assert.That(DtcWire.TypeOf(connection.ReadFrame()), Is.EqualTo(DtcConstants.MARKET_DATA_UPDATE_TRADE_V2));
		Assert.That(DtcWire.TypeOf(connection.ReadFrame()), Is.EqualTo(DtcConstants.MARKET_DATA_UPDATE_TRADE_V2));
		Assert.That(DtcWire.TypeOf(connection.ReadFrame()), Is.EqualTo(DtcConstants.TRADING_SYMBOL_STATUS));
		Assert.That(queue.Count, Is.Zero);
		Assert.That(queue.TakeDropped(), Is.Zero);
	}

	[Test]
	public void TradingStatusFenceWaitsBehindBusyMarketWriter()
	{
		using var connection = Connect(new FakeHistory(0), onlyHistoryServer: false);
		Logon(connection, 0);
		var queue = new SessionMarketDataQueue(8);
		var session = connection.ServerSession;
		var state = InstallMarketState(session, queue, 1);
		lock (SessionField("_sessionLock").GetValue(session)!)
		{
			SessionField("_marketSending").SetValue(session, true);
			queue.Enqueue(1, TradeFrame(1, 12.5));
			queue.Enqueue(1, TradeFrame(1, 12.6));
			ReceiveMarket(session, state, 1, new HubUpdate(new TInvestStatus("synthetic-live-uid", TInvestTradingStatus.BreakInTrading)));
			Assert.That(queue.Count, Is.EqualTo(3), "The fence must stay bounded behind a busy writer, not move trades to the socket backlog.");
			SessionField("_marketSending").SetValue(session, false);
			typeof(Session).GetMethod("PumpMarketData", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null);
		}
		Assert.That(DtcWire.TypeOf(connection.ReadFrame()), Is.EqualTo(DtcConstants.MARKET_DATA_UPDATE_TRADE_V2));
		Assert.That(DtcWire.TypeOf(connection.ReadFrame()), Is.EqualTo(DtcConstants.MARKET_DATA_UPDATE_TRADE_V2));
		Assert.That(DtcWire.TypeOf(connection.ReadFrame()), Is.EqualTo(DtcConstants.TRADING_SYMBOL_STATUS));
	}

	[Test]
	public void TradingStatusFenceAtCapacityClosesInsteadOfGrowingTheSocketBacklog()
	{
		using var connection = Connect(new FakeHistory(0), onlyHistoryServer: false);
		Logon(connection, 0);
		var queue = new SessionMarketDataQueue(2);
		var session = connection.ServerSession;
		var state = InstallMarketState(session, queue, 1);
		lock (SessionField("_sessionLock").GetValue(session)!)
		{
			SessionField("_marketSending").SetValue(session, true);
			queue.Enqueue(1, TradeFrame(1, 12.5));
			queue.Enqueue(1, TradeFrame(1, 12.6));
			ReceiveMarket(session, state, 1, new HubUpdate(new TInvestStatus("synthetic-live-uid", TInvestTradingStatus.BreakInTrading)));
			Assert.That(SessionField("_closing").GetValue(session), Is.EqualTo(true), "If the non-droppable fence cannot fit, connection loss must invalidate continuity.");
		}
	}

	static FieldInfo SessionField(string name) => typeof(Session).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

	static object InstallMarketState(Session session, SessionMarketDataQueue queue, uint id)
	{
		var type = typeof(Session).GetNestedType("MarketSubscription", BindingFlags.NonPublic)!;
		var state = Activator.CreateInstance(type, new object?[] { new CancellationTokenSource(), null })!;
		((IDictionary)SessionField("_marketSubscriptions").GetValue(session)!)[id] = state;
		SessionField("_marketQueue").SetValue(session, queue);
		return state;
	}

	static void ReceiveMarket(Session session, object state, uint id, HubEvent message)
		=> typeof(Session).GetMethod("ReceiveMarketEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(session, new[] { (object)id, state, 1, message });

	static byte[] TradeFrame(uint id, double price)
	{
		var encoder = new BinaryEncoder();
		encoder.EncodeTInvestUpdate(id, new TInvestTrade("synthetic-live-uid", new(100, 0), price, 2, TInvestDirection.Buy, true), 1);
		return encoder.GetEncodedMessage();
	}
}
