namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Client;
using Dtc.Protocol;
using Dtc.Protocol.Messages;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Market;

[TestFixture]
[NonParallelizable]
public class TInvestLiveWireTests
{
	[TestCase("SYNTH", "TESTCC")]
	[TestCase("SYNTH-TESTCC", "")]
	public async Task RealClientReadsSnapshotV2TradesQuotesStatusAndUnsubscribe(string symbol, string exchange)
	{
		var clock = new ManualTimeProvider();
		var uid = ScriptedTInvestGateway.Invent().Short.Uid;
		using var gateway = new ScriptedTInvestGateway
		{
			Snapshot = (_, _) => Task.FromResult(new TInvestMarketSnapshot(12.5, new(100, 123456789), new(uid, new(100, 123456789), 12.4, 2, 12.6, 3), TInvestTradingStatus.NormalTrading))
		};
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var host = new TInvestTestHost(gateway, clock);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		var subscribe = client.SubscribeMarketDataAsync(symbol, exchange);
		await TInvestMarketDataHubTests.Pump(clock, () => subscribe.IsCompleted);
		var id = await subscribe;
		var snapshot = (DtcMarketDataSnapshot)(await Read(client, e => e.MarketData.Kind == DtcMarketDataKind.Snapshot)).Record!;
		Assert.That(snapshot.LastTradePrice, Is.EqualTo(12.5));
		Assert.That(snapshot.BidQuantity, Is.EqualTo(20));
		Assert.That(snapshot.AskQuantity, Is.EqualTo(30));
		Assert.That(double.IsNaN(snapshot.SessionOpenPrice) && double.IsNaN(snapshot.LastTradeVolume), Is.True, "Missing fields stay unset, not fabricated zeroes.");
		var time = new TInvestTimestamp(101, 123456789);
		var trade = new TInvestTrade(uid, time, 12.6, 4, TInvestDirection.Buy, true);
		gateway.Streams[0].Emit(trade);
		gateway.Streams[0].Emit(trade with
		{
			Time = time with
			{
				Nanos = time.Nanos + 1
			}
		});
		gateway.Streams[0].Emit(new TInvestQuote(uid, time, 12.5, 5, null, null));
		var first = (await Read(client, e => e.MarketData.Kind == DtcMarketDataKind.Trade)).MarketData;
		var second = (await Read(client, e => e.MarketData.Kind == DtcMarketDataKind.Trade)).MarketData;
		Assert.That(first.SymbolId, Is.EqualTo(id));
		Assert.That(first.Quantity, Is.EqualTo(40));
		Assert.That(first.Side, Is.EqualTo(DtcSide.Ask));
		Assert.That(first.Time, Is.EqualTo(DtcTimestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddTicks(time.Microseconds * TimeSpan.TicksPerMicrosecond))));
		Assert.That(second.Time, Is.EqualTo(first.Time), "Equal microseconds are preserved without deduplicating trades.");
		var quote = (await Read(client, e => e.MarketData.Kind == DtcMarketDataKind.BidAsk)).MarketData;
		Assert.That(quote.BidQuantity, Is.EqualTo(50));
		Assert.That(double.IsNaN(quote.AskPrice), Is.True);
		gateway.Streams[0].Emit(new TInvestStatus(uid, TInvestTradingStatus.BreakInTrading));
		var status = await Read(client, e => e.MessageType == DtcConstants.TRADING_SYMBOL_STATUS);
		Assert.That(((TradingSymbolStatus)status.Record!).Status, Is.EqualTo(TradingStatusEnum.TRADING_STATUS_TRADING_HALT));
		await client.UnsubscribeMarketDataAsync(id);
		await TInvestMarketDataHubTests.Until(() => gateway.Streams[0].Disposed);
	}

	[Test]
	public async Task RealClientResubscribesSameIdAfterUnavailableAndAvailable()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var host = new TInvestTestHost(gateway, clock);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		var subscribe = client.SubscribeMarketDataAsync("SYNTH", "TESTCC");
		await TInvestMarketDataHubTests.Pump(clock, () => subscribe.IsCompleted);
		var id = await subscribe;
		await Read(client, e => e.MarketData.Kind == DtcMarketDataKind.Snapshot);
		gateway.Streams[0].Break();
		var unavailable = (MarketDataFeedSymbolStatus)(await Read(client, e => e.MessageType == DtcConstants.MARKET_DATA_FEED_SYMBOL_STATUS)).Record!;
		Assert.That(unavailable.Status, Is.EqualTo(MarketDataFeedStatusEnum.MARKET_DATA_FEED_UNAVAILABLE));
		await TInvestMarketDataHubTests.Pump(clock, () => gateway.Streams.Count >= 2 && gateway.Streams[1].Queries > 0);
		var available = (MarketDataFeedSymbolStatus)(await Read(client, e => e.MessageType == DtcConstants.MARKET_DATA_FEED_SYMBOL_STATUS)).Record!;
		Assert.That(available.Status, Is.EqualTo(MarketDataFeedStatusEnum.MARKET_DATA_FEED_AVAILABLE));
		var next = await Read(client, e => e.MarketData.Kind == DtcMarketDataKind.Snapshot);
		Assert.That(next.MarketData.SymbolId, Is.EqualTo(id));
		Assert.That(gateway.Streams.Count, Is.EqualTo(2), "Refreshing a SymbolID must not momentarily release its last upstream reference.");
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task SnapshotOnlyAvoidsNewSubscriptionsAndPreservesExistingSymbolId(bool alreadySubscribed)
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var host = new TInvestTestHost(gateway, clock);
		using var client = new TcpClient();
		client.Connect(IPAddress.Loopback, host.Port);
		host.WaitUntilConnected();
		client.ReceiveTimeout = 5000;
		var bytes = new byte[256];
		if (alreadySubscribed)
		{
			var subscribeLength = new MarketDataRequest
			{
				SymbolID = 9,
				Symbol = "SYNTH",
				Exchange = "TESTCC",
				RequestAction = RequestActionEnum.SUBSCRIBE
			}.ToVls(bytes, Encoding.UTF8);
			client.GetStream().Write(bytes.AsSpan(0, subscribeLength));
			await TInvestMarketDataHubTests.Pump(clock, () => client.Available > 0);
			Frame(client);
		}

		var length = new MarketDataRequest
		{
			SymbolID = 9,
			Symbol = "SYNTH",
			Exchange = "TESTCC",
			RequestAction = RequestActionEnum.SNAPSHOT
		}.ToVls(bytes, Encoding.UTF8);
		client.GetStream().Write(bytes.AsSpan(0, length));
		await TInvestMarketDataHubTests.Pump(clock, () => client.Available > 0);
		var frame = Frame(client);
		DtcBinaryDecoder.Decode(frame, out s_MarketDataSnapshot snapshot);
		Assert.That(snapshot.SymbolID, Is.EqualTo(9));
		Assert.That(gateway.Streams.Count, Is.EqualTo(alreadySubscribed ? 1 : 0));
		if (alreadySubscribed)
		{
			gateway.Streams[0].Emit(new TInvestTrade(gateway.Instruments[0].Short.Uid, new(1, 0), 12, 1, TInvestDirection.Buy, true));
			await TInvestMarketDataHubTests.Pump(clock, () => client.Available > 0);
			DtcBinaryDecoder.Decode(Frame(client), out s_MarketDataUpdateTradeV2 trade);
			Assert.That(trade.SymbolID, Is.EqualTo(9));
			Assert.That(gateway.Streams[0].Disposed, Is.False);
		}
	}

	[Test]
	public async Task SubscriptionTimeoutIsARejectOnTheWireNotAnUnansweredRequest()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway
		{
			Snapshot = (_, ct) => new TaskCompletionSource<TInvestMarketSnapshot>().Task.WaitAsync(ct)
		};
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var host = new TInvestTestHost(gateway, clock);
		await using var client = host.CreateClient(heartbeatInterval: 60);
		await host.ConnectAsync(client);
		var task = client.SubscribeMarketDataAsync("SYNTH", "TESTCC");
		await TInvestMarketDataHubTests.Pump(clock, () => gateway.Streams.Count != 0 && gateway.Streams[0].Queries > 0);
		clock.Advance(TimeSpan.FromSeconds(20));
		var error = Assert.ThrowsAsync<DtcMarketDataRejectedException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.That(error!.Message, Does.Contain("retry later"));
	}

	static async Task<DtcClientEvent> Read(DtcClient client, Func<DtcClientEvent, bool> predicate)
	{
		using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (true)
		{
			var item = await client.Events.ReadAsync(stop.Token);
			if (predicate(item))
			{
				return item;
			}
		}
	}

	static byte[] Frame(TcpClient client)
	{
		var header = new byte[4];
		client.GetStream().ReadExactly(header);
		var frame = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header)];
		header.CopyTo(frame, 0);
		client.GetStream().ReadExactly(frame.AsSpan(4));
		return frame;
	}
}
