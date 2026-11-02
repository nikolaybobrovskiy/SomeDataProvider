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
using Dtc.Protocol.Vls;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Data;

[TestFixture]
[NonParallelizable]
public class TInvestWireTests
{
	[TestCase(TInvestInstrumentKind.Share, SecurityTypeEnum.SECURITY_TYPE_STOCK, true)]
	[TestCase(TInvestInstrumentKind.Etf, SecurityTypeEnum.SECURITY_TYPE_STOCK, true)]
	[TestCase(TInvestInstrumentKind.Bond, SecurityTypeEnum.SECURITY_TYPE_BOND, true)]
	[TestCase(TInvestInstrumentKind.Future, SecurityTypeEnum.SECURITY_TYPE_FUTURES, true)]
	[TestCase(TInvestInstrumentKind.Currency, SecurityTypeEnum.SECURITY_TYPE_FOREX, true)]
	[TestCase(TInvestInstrumentKind.StockOption, SecurityTypeEnum.SECURITY_TYPE_STOCK_OPTION, false)]
	[TestCase(TInvestInstrumentKind.FuturesOption, SecurityTypeEnum.SECURITY_TYPE_FUTURES_OPTION, false)]
	[TestCase(TInvestInstrumentKind.IndexOption, SecurityTypeEnum.SECURITY_TYPE_INDEX_OPTION, false)]
	public async Task DefinitionsRoundTripEveryKindThroughRealClient(TInvestInstrumentKind kind, SecurityTypeEnum expected, bool hasDepth)
	{
		using var gateway = new ScriptedTInvestGateway();
		var expiry = new DateTimeOffset(2028, 6, 15, 12, 0, 0, TimeSpan.Zero);
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent(kind: kind) with { Expiration = expiry });
		using var host = new TInvestTestHost(gateway);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		var definition = (await client.GetSecurityDefinitionAsync("SYNTH", "TESTCC"))!;
		Assert.That(definition.Symbol, Is.EqualTo("SYNTH"));
		Assert.That(definition.Exchange, Is.EqualTo("TESTCC"));
		Assert.That(definition.SecurityType, Is.EqualTo(expected));
		Assert.That(definition.MinPriceIncrement, Is.EqualTo((double)(float)0.01));
		Assert.That(definition.Currency, Is.EqualTo("ZZZ"));
		Assert.That(definition.Description, Does.Contain("lot 10"));
		Assert.That(definition.ExchangeSymbol, Is.EqualTo("SYNTH_TESTCC"));
		Assert.That(definition.PriceDisplayFormat, Is.EqualTo(PriceDisplayFormatEnum.PRICE_DISPLAY_FORMAT_DECIMAL_2));
		Assert.That(definition.SecurityExpirationDate, Is.EqualTo(DtcTimestamp.FromDateTimeOffset(expiry)));
		Assert.That(definition.HasMarketDepthData, Is.EqualTo(hasDepth));
	}

	[TestCase(10, 0, 10)]
	[TestCase(1, 1, 1)]
	[TestCase(3, 3, 10)]
	[TestCase(100, 50, 50)]
	public async Task RealDtcClientCompletesEmptyOneAndMultipleLevelDepthSnapshots(int requestedLevels, int bookLevels, int expectedUpstreamDepth)
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		var bids = Enumerable.Range(0, bookLevels).Select(i => new TInvestBookLevel(100 - i, 20 + i)).ToArray();
		var asks = Enumerable.Range(0, bookLevels).Select(i => new TInvestBookLevel(101 + i, 30 + i)).ToArray();
		gateway.OrderBook = (uid, depth, _) => Task.FromResult(new TInvestOrderBook(uid, new(100, 123_456_789), depth, bids, asks));
		using var host = new TInvestTestHost(gateway);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);

		var symbolId = await client.SubscribeMarketDepthAsync("SYNTH", "TESTCC", requestedLevels).WaitAsync(TimeSpan.FromSeconds(10));
		var book = new DtcMarketDepthBook(Math.Max(10, requestedLevels));
		var snapshotComplete = false;
		var depthFrameCount = 0;
		var firstBatch = DtcBatchBoundary.None;
		var lastBatch = DtcBatchBoundary.None;
		while (!snapshotComplete)
		{
			var item = await client.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
			if (item.Kind != DtcClientEventKind.MarketData || item.MarketData.SymbolId != symbolId || item.MarketData.Kind != DtcMarketDataKind.Depth)
			{
				continue;
			}

			depthFrameCount++;
			if (depthFrameCount == 1)
			{
				firstBatch = item.MarketData.Batch;
			}
			lastBatch = item.MarketData.Batch;
			book.Apply(item.MarketData);
			snapshotComplete = !book.IsInSnapshot;
		}

		Assert.That(book.BidCount, Is.EqualTo(bookLevels));
		Assert.That(book.AskCount, Is.EqualTo(bookLevels));
		Assert.That(firstBatch.HasFlag(DtcBatchBoundary.Begin), Is.True);
		Assert.That(lastBatch.HasFlag(DtcBatchBoundary.End), Is.True);
		Assert.That(depthFrameCount, Is.EqualTo(bookLevels == 0 ? 2 : 2 * bookLevels + 1));
		if (bookLevels > 0)
		{
			Assert.That(book.Bids[0].Price, Is.EqualTo(100));
			Assert.That(book.Asks[0].Price, Is.EqualTo(101));
		}
		Assert.That(gateway.OrderBookRequests.Single().Depth, Is.EqualTo(expectedUpstreamDepth));
	}

	[TestCase(0)]
	[TestCase(1)]
	[TestCase(3)]
	public async Task RealDtcClientReplacementSnapshotClearsAStalledPreviousBatch(int replacementLevels)
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		var uid = gateway.Instruments.Single().Short.Uid;
		var initialBids = new[] { new TInvestBookLevel(100, 2), new TInvestBookLevel(99, 1), new TInvestBookLevel(98, 1) };
		var initialAsks = new[] { new TInvestBookLevel(101, 2), new TInvestBookLevel(102, 1), new TInvestBookLevel(103, 1) };
		var replacementBids = Enumerable.Range(0, replacementLevels).Select(i => new TInvestBookLevel(200 - i, 5 + i)).ToArray();
		var replacementAsks = Enumerable.Range(0, replacementLevels).Select(i => new TInvestBookLevel(201 + i, 6 + i)).ToArray();
		var calls = 0;
		gateway.OrderBook = (id, depth, _) =>
		{
			var call = Interlocked.Increment(ref calls);
			return Task.FromResult(new TInvestOrderBook(id, new(call == 1 ? 100 : 102, 0), depth,
				call == 1 ? initialBids : replacementBids, call == 1 ? initialAsks : replacementAsks));
		};
		using var host = new TInvestTestHost(gateway);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		var symbolId = await client.SubscribeMarketDepthAsync("SYNTH", "TESTCC", 3).WaitAsync(TimeSpan.FromSeconds(10));
		var book = new DtcMarketDepthBook(3);
		var oldBatch = new System.Collections.Generic.List<DtcMarketDataEvent>();
		while (true)
		{
			var item = await client.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
			if (item.Kind != DtcClientEventKind.MarketData || item.MarketData.SymbolId != symbolId || item.MarketData.Kind != DtcMarketDataKind.Depth)
			{
				continue;
			}

			oldBatch.Add(item.MarketData);
			if (item.MarketData.Batch.HasFlag(DtcBatchBoundary.End))
			{
				break;
			}
		}

		foreach (var frame in oldBatch.Where(frame => !frame.Batch.HasFlag(DtcBatchBoundary.End)))
		{
			book.Apply(frame);
		}
		Assert.That(book.IsInSnapshot, Is.True, "Simulate the caller losing the previous FINAL_UPDATE_TRUE marker.");
		Assert.That(book.BidCount, Is.EqualTo(3));

		gateway.Streams.Single().Emit(new TInvestOrderBook(uid, new(101, 0), 10,
			new[] { new TInvestBookLevel(105, 2), new TInvestBookLevel(105, 3) }, initialAsks, false));
		var complete = false;
		while (!complete)
		{
			var item = await client.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
			if (item.Kind != DtcClientEventKind.MarketData || item.MarketData.SymbolId != symbolId || item.MarketData.Kind != DtcMarketDataKind.Depth)
			{
				continue;
			}

			book.Apply(item.MarketData);
			complete = item.MarketData.Batch.HasFlag(DtcBatchBoundary.End);
		}

		Assert.That(gateway.OrderBookRequests.Count, Is.EqualTo(2));
		Assert.That(book.IsInSnapshot, Is.False);
		Assert.That(book.BidCount, Is.EqualTo(replacementLevels));
		Assert.That(book.AskCount, Is.EqualTo(replacementLevels));
		if (replacementLevels > 0)
		{
			Assert.That(book.Bids[0].Price, Is.EqualTo(200));
			Assert.That(book.Asks[0].Price, Is.EqualTo(201));
		}
	}

	[Test]
	public async Task RealDtcClientAppliesDepthUpdatesWithMillisecondTimestamps()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		var uid = gateway.Instruments.Single().Short.Uid;
		var initialBids = new[] { new TInvestBookLevel(100, 2), new TInvestBookLevel(99, 1) };
		var initialAsks = new[] { new TInvestBookLevel(101, 2), new TInvestBookLevel(102, 1) };
		gateway.OrderBook = (uid, depth, _) => Task.FromResult(new TInvestOrderBook(uid, new(100, 0), depth, initialBids, initialAsks));
		using var host = new TInvestTestHost(gateway);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);

		var symbolId = await client.SubscribeMarketDepthAsync("SYNTH", "TESTCC", 3).WaitAsync(TimeSpan.FromSeconds(10));
		var book = new DtcMarketDepthBook(3);
		var snapshotComplete = false;
		while (!snapshotComplete)
		{
			var item = await client.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
			if (item.Kind == DtcClientEventKind.MarketData && item.MarketData.SymbolId == symbolId && item.MarketData.Kind == DtcMarketDataKind.Depth)
			{
				book.Apply(item.MarketData);
				snapshotComplete = !book.IsInSnapshot;
			}
		}

		gateway.Streams.Single().Emit(new TInvestOrderBook(uid, new(101, 123_456_789), 10,
			new[] { new TInvestBookLevel(101, 5), new TInvestBookLevel(100, 2) }, initialAsks));
		var updateCount = 0;
		long? updateTime = null;
		while (updateCount < 2)
		{
			var item = await client.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
			if (item.Kind != DtcClientEventKind.MarketData || item.MarketData.SymbolId != symbolId || item.MarketData.Kind != DtcMarketDataKind.Depth || item.MarketData.DepthAction == DtcDepthAction.Snapshot)
			{
				continue;
			}

			book.Apply(item.MarketData);
			updateCount++;
			updateTime = item.MarketData.Time?.UnixMicroseconds;
		}

		Assert.That(updateTime, Is.EqualTo(101_123_000));
		Assert.That(book.IsInUpdateBatch, Is.False);
		Assert.That(book.BestBid?.Price, Is.EqualTo(101));
		Assert.That(gateway.OrderBookRequests.Single().Depth, Is.EqualTo(10));
	}

	[Test]
	public async Task RealDtcClientGetsFullSnapshotAfterDepthQueueOverflowBeforeLaterUpdate()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		var uid = gateway.Instruments.Single().Short.Uid;
		var initialBids = Enumerable.Range(0, 50).Select(i => new TInvestBookLevel(1000 - i, 1 + i)).ToArray();
		var initialAsks = Enumerable.Range(0, 50).Select(i => new TInvestBookLevel(2000 + i, 1 + i)).ToArray();
		gateway.OrderBook = (id, depth, _) => Task.FromResult(new TInvestOrderBook(id, new(100, 0), depth, initialBids, initialAsks));
		using var host = new TInvestTestHost(gateway, options: new TInvestOptions { SessionQueueLimit = 128 });
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		var symbolId = await client.SubscribeMarketDepthAsync("SYNTH", "TESTCC", 50).WaitAsync(TimeSpan.FromSeconds(10));
		var book = new DtcMarketDepthBook(50);

		async Task<DtcMarketDataEvent> NextDepthAsync()
		{
			while (true)
			{
				var item = await client.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
				if (item.Kind == DtcClientEventKind.MarketData && item.MarketData.SymbolId == symbolId && item.MarketData.Kind == DtcMarketDataKind.Depth)
				{
					return item.MarketData;
				}
			}
		}

		var initialComplete = false;
		while (!initialComplete)
		{
			var item = await NextDepthAsync();
			book.Apply(item);
			initialComplete = !book.IsInSnapshot;
		}

		var overflowBids = Enumerable.Range(0, 50).Select(i => new TInvestBookLevel(3000 - i, 50 + i)).ToArray();
		var overflowAsks = Enumerable.Range(0, 50).Select(i => new TInvestBookLevel(4000 + i, 50 + i)).ToArray();
		gateway.Streams.Single().Emit(new TInvestOrderBook(uid, new(101, 0), 50, overflowBids, overflowAsks));
		var deltasBeforeRepair = 0;
		var repairComplete = false;
		while (!repairComplete)
		{
			var item = await NextDepthAsync();
			if (item.DepthAction != DtcDepthAction.Snapshot)
			{
				deltasBeforeRepair++;
			}
			book.Apply(item);
			repairComplete = item.DepthAction == DtcDepthAction.Snapshot && !book.IsInSnapshot;
		}

		Assert.That(deltasBeforeRepair, Is.Zero, "No partial delta batch may escape after the queue discards an oversized update.");
		Assert.That(book.BidCount, Is.EqualTo(50));
		Assert.That(book.AskCount, Is.EqualTo(50));
		Assert.That(book.BestBid?.Price, Is.EqualTo(3000));

		var laterBids = overflowBids.ToArray();
		laterBids[0] = laterBids[0] with { Lots = 777 };
		gateway.Streams.Single().Emit(new TInvestOrderBook(uid, new(102, 0), 50, laterBids, overflowAsks));
		var laterUpdate = await NextDepthAsync();
		Assert.That(laterUpdate.DepthAction, Is.EqualTo(DtcDepthAction.Update));
		book.Apply(laterUpdate);
		Assert.That(book.BestBid?.Quantity, Is.EqualTo(7770));
		Assert.That(book.BestBid?.Price, Is.EqualTo(3000));
	}

	[TestCase("SYNTH", "TESTCC")]
	[TestCase("SYNTH-TESTCC", "")]
	public async Task ExactDefinitionEchoesRequestedFormAndLogonAdvertisesMarketDepth(string symbol, string exchange)
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var host = new TInvestTestHost(gateway);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		var definition = (await client.GetSecurityDefinitionAsync(symbol, exchange))!;
		Assert.That(definition.Symbol, Is.EqualTo(symbol));
		Assert.That(definition.Exchange, Is.EqualTo(exchange));
		Assert.That(client.LogonResponse!.ResultText, Does.Contain("FRED, T-Invest (sandbox)"));
		Assert.That(client.LogonResponse.SymbolExchangeDelimiter, Is.Empty);
		Assert.That(client.Capabilities!.MarketDepthIsSupported, Is.True);
		Assert.That(client.Capabilities.ResubscribeWhenMarketDataFeedAvailable, Is.True);
		Assert.That(client.Capabilities.TradingIsSupported, Is.False);
	}

	[Test]
	public async Task HistoryOnlyListenerDoesNotAdvertiseMarketDepth()
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, onlyHistoryServer: true);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);

		Assert.That(client.Capabilities!.MarketDepthIsSupported, Is.False);
	}

	[Test]
	public async Task SearchThenExactUsesCompleteClientCacheAndLiveSubscriptionCompletes()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.AddRange(Enumerable.Range(0, 50).Select(i => ScriptedTInvestGateway.Invent("SYNTH" + i)));
		using var host = new TInvestTestHost(gateway);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		var rows = await client.SearchSymbolsAsync(new SymbolSearchRequest { SearchText = "SYNTH", Exchange = "TESTCC", SearchType = SearchTypeEnum.SEARCH_TYPE_BY_SYMBOL });
		Assert.That(rows.Length, Is.EqualTo(20));
		Assert.That(rows.All(row => row.MinPriceIncrement > 0 && row.Currency == "ZZZ"), Is.True);
		var calls = gateway.Lookups.Count;
		var exact = (await client.GetSecurityDefinitionAsync(rows[0].Symbol, rows[0].Exchange))!;
		Assert.That(exact.MinPriceIncrement, Is.GreaterThan(0));
		Assert.That(gateway.Lookups.Count, Is.EqualTo(calls));
		var subscription = await client.SubscribeMarketDataAsync(rows[0].Symbol, rows[0].Exchange);
		Assert.That(subscription, Is.GreaterThan(0));
	}

	[TestCase(102, 103)]
	[TestCase(120, 121)]
	public void MarketDataAndDepthGetCleanWireRejects(int requestType, int rejectType)
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway);
		using var client = new TcpClient();
		client.Connect(IPAddress.Loopback, host.Port);
		host.WaitUntilConnected();
		client.ReceiveTimeout = 10000;
		var request = new byte[256];
		var length = requestType == 102
			? new MarketDataRequest { SymbolID = 7, Symbol = "UNKNOWN", Exchange = "TESTCC", RequestAction = RequestActionEnum.SUBSCRIBE }.ToVls(request, Encoding.UTF8)
			: new MarketDepthRequest { SymbolID = 7, Symbol = "SYNTH-TESTCC", RequestAction = RequestActionEnum.SUBSCRIBE, NumLevels = 10 }.ToVls(request, Encoding.UTF8);
		client.GetStream().Write(request.AsSpan(0, length));
		var response = ReadFrame(client);
		Assert.That(DtcWire.TypeOf(response), Is.EqualTo(rejectType));
		if (requestType == 102)
		{
			DtcVlsDecoder.Decode(response, out Dtc.Protocol.Vls.s_MarketDataReject reject);
			var text = DtcVlsDecoder.GetString(response, reject.RejectText, Encoding.UTF8);
			Assert.That(text, Does.Contain("UNKNOWN"));
			Assert.That(text.Any(char.IsControl), Is.False);
		}
	}

	[TestCase(TInvestInstrumentKind.StockOption)]
	[TestCase(TInvestInstrumentKind.FuturesOption)]
	[TestCase(TInvestInstrumentKind.IndexOption)]
	public void UnsupportedOptionsGetMarketDepthReject(TInvestInstrumentKind kind)
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent(kind: kind));
		using var host = new TInvestTestHost(gateway);
		using var client = new TcpClient();
		client.Connect(IPAddress.Loopback, host.Port);
		host.WaitUntilConnected();
		client.ReceiveTimeout = 10000;
		var request = new byte[256];
		var length = new MarketDepthRequest { SymbolID = 7, Symbol = "SYNTH-TESTCC", RequestAction = RequestActionEnum.SUBSCRIBE, NumLevels = 10 }.ToVls(request, Encoding.UTF8);
		client.GetStream().Write(request.AsSpan(0, length));
		var response = ReadFrame(client);
		Assert.That(DtcWire.TypeOf(response), Is.EqualTo(121));
		DtcVlsDecoder.Decode(response, out Dtc.Protocol.Vls.s_MarketDepthReject reject);
		var text = DtcVlsDecoder.GetString(response, reject.RejectText, Encoding.UTF8);
		Assert.That(text, Does.Contain("not supported"));
	}

	[Test]
	public void HistoryGetsCleanStageReject()
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway);
		using var client = new TcpClient();
		client.Connect(IPAddress.Loopback, host.Port);
		host.WaitUntilConnected();
		client.ReceiveTimeout = 10000;
		var request = new byte[256];
		var length = new HistoricalPriceDataRequest { RequestID = 71, Symbol = "UNKNOWN", Exchange = "TESTCC" }.ToVls(request, Encoding.UTF8);
		client.GetStream().Write(request.AsSpan(0, length));
		var response = ReadFrame(client);
		Assert.That(DtcWire.TypeOf(response), Is.EqualTo(802));
		DtcVlsDecoder.Decode(response, out Dtc.Protocol.Vls.s_HistoricalPriceDataReject reject);
		var text = DtcVlsDecoder.GetString(response, reject.RejectText, Encoding.UTF8);
		Assert.That(text, Does.Contain("UNKNOWN"));
		Assert.That(text.Any(char.IsControl), Is.False);
	}

	[Test]
	public async Task UnicodeNamesAndQueriesRoundTripThroughTheRealClient()
	{
		using var gateway = new ScriptedTInvestGateway();
		var instrument = ScriptedTInvestGateway.Invent();
		gateway.Instruments.Add(instrument with { Short = instrument.Short with { Name = "Придуманный инструмент" } });
		using var host = new TInvestTestHost(gateway);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		var rows = await client.SearchSymbolsAsync(new SymbolSearchRequest { SearchText = "Придуманный", Exchange = "TESTCC" });
		Assert.That(gateway.LastQuery, Is.EqualTo("Придуманный"));
		Assert.That(rows.Single().Description, Is.EqualTo("Придуманный инструмент (lot 10)"));
	}

	static byte[] ReadFrame(TcpClient client)
	{
		var stream = client.GetStream();
		var header = new byte[4];
		stream.ReadExactly(header);
		var frame = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header)];
		header.CopyTo(frame, 0);
		stream.ReadExactly(frame.AsSpan(4));
		return frame;
	}
}

sealed class TInvestTestHost : IDisposable
{
	readonly StoresProvider _provider;
	readonly ConnectedServer _server;

	public TInvestTestHost(ITInvestGateway gateway, TimeProvider? clock = null, TInvestOptions? options = null, bool onlyHistoryServer = false)
	{
		_provider = new StoresProvider(null, new TInvestStore(gateway, options ?? new TInvestOptions(), clock), NullLoggerFactory.Instance);
		_server = new ConnectedServer(_provider, clock, onlyHistoryServer);
		if (!_server.Start()) throw new InvalidOperationException("Loopback test server could not start.");
	}

	public int Port => ((IPEndPoint)_server.Endpoint).Port;
	public Task BarrierAsync() => _server.MarketDataHub!.BarrierAsync();

	public async Task ConnectAsync(DtcClient client)
	{
		await client.ConnectAsync();
		WaitUntilConnected();
	}

	public void WaitUntilConnected() => Assert.That(_server.Connected.Wait(TimeSpan.FromSeconds(10)), Is.True, "The test server did not finish accepting the connection.");

	public DtcClient CreateClient(int heartbeatInterval = 10) => new(new DtcClientSettings
	{
		Host = "127.0.0.1",
		Port = Port,
		// The published provider-v1 profile deliberately refuses broker symbols; MP-1 is later.
		Profile = DtcServerProfile.Generic,
		Encoding = EncodingEnum.BINARY_WITH_VARIABLE_LENGTH_STRINGS,
		SynchronizeTrading = false,
		Reconnect = false,
		HeartbeatIntervalInSeconds = heartbeatInterval,
	});

	public void Dispose()
	{
		_server.Stop();
		_server.Dispose();
		_provider.Dispose();
	}

	sealed class ConnectedServer(StoresProvider provider, TimeProvider? clock, bool onlyHistoryServer) : Server(IPAddress.Loopback, 0, onlyHistoryServer, provider, provider, provider, NullLoggerFactory.Instance, clock)
	{
		public ManualResetEventSlim Connected { get; } = new();

		protected override void OnConnected(NetCoreServer.TcpSession session)
		{
			base.OnConnected(session);
			Connected.Set();
		}
	}
}
