namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Market;

[TestFixture]
public class TInvestMarketDataHubTests
{
	[Test]
	public async Task ReferencesAreSharedAndChangesCoalesceByKind()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		using var recorder = new TInvestTickRecorder(null, clock);
		using var hub = new TInvestMarketDataHub(gateway, new TInvestOptions(), clock, recorder, () => 0.5);
		var one = hub.SubscribeAsync(Symbol(), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default);
		var two = hub.SubscribeAsync(Symbol(), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default);
		var other = hub.SubscribeAsync(Symbol("OTHER"), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default);
		await Pump(clock, () => one.IsCompleted && two.IsCompleted && other.IsCompleted);
		using var a = await one;
		using var b = await two;
		using var c = await other;
		Assert.That(gateway.Streams.Count, Is.EqualTo(1));
		var stream = gateway.Streams.Single();
		var initial = Requests(stream).Where(r => r.Kind != TInvestSubscriptionKind.All && r.Subscribe).ToArray();
		Assert.That(initial.Length, Is.EqualTo(3));
		Assert.That(initial.All(r => r.Uids.Count == 2), Is.True);
		a.Dispose();
		await hub.BarrierAsync();
		Assert.That(Requests(stream).Any(r => !r.Subscribe), Is.False, "The second subscriber still owns the upstream reference.");
		b.Dispose();
		await Pump(clock, () => Requests(stream).Count(r => !r.Subscribe) == 3);
		Assert.That(Requests(stream).Where(r => !r.Subscribe).All(r => r.Uids.Single() == Symbol().Instrument.Short.Uid), Is.True);
		c.Dispose();
		await hub.BarrierAsync();
		await Until(() => stream.Disposed);
	}

	[TestCase(TInvestSubscriptionStatus.InstrumentNotFound, "Unknown instrument")]
	[TestCase(TInvestSubscriptionStatus.LimitIsExceeded, "Server subscription limit")]
	[TestCase(TInvestSubscriptionStatus.TooManyRequests, "T-Invest subscription limit")]
	[TestCase(TInvestSubscriptionStatus.InternalError, "subscription rejected")]
	public async Task PerElementFailureDoesNotRejectOtherElements(TInvestSubscriptionStatus status, string text)
	{
		var clock = new ManualTimeProvider();
		var stream = new ScriptedMarketDataStream
		{
			Status = (_, uid) => uid == Symbol("BAD").Instrument.Short.Uid ? status : TInvestSubscriptionStatus.Success
		};
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ => Task.FromResult<ITInvestMarketDataStream>(stream)
		};
		using var hub = NewHub(gateway, clock);
		var good = hub.SubscribeAsync(Symbol(), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default);
		var bad = hub.SubscribeAsync(Symbol("BAD"), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default);
		await Pump(clock, () => good.IsCompleted && bad.IsCompleted);
		using var subscription = await good;
		var error = Assert.ThrowsAsync<TInvestRequestException>(async () => await bad);
		Assert.That(error!.Message, Does.Contain(text));
		Assert.That(stream.Disposed, Is.False);
	}

	[Test]
	public async Task TwentySecondDeadlineWithdrawsAnUnansweredSubscription()
	{
		var clock = new ManualTimeProvider();
		var stream = new ScriptedMarketDataStream
		{
			AutoReply = false
		};
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ => Task.FromResult<ITInvestMarketDataStream>(stream)
		};
		using var hub = NewHub(gateway, clock);
		var task = hub.SubscribeAsync(Symbol(), _ => Assert.Fail("No snapshot before upstream admission."), clock.GetUtcNow().AddSeconds(20), default);
		await hub.BarrierAsync();
		await Pump(clock, () => Requests(stream).Length != 0);
		clock.Advance(TimeSpan.FromSeconds(20) - (clock.GetUtcNow() - DateTimeOffset.UnixEpoch));
		var error = Assert.ThrowsAsync<TInvestRequestException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		await hub.BarrierAsync();
		await Until(() => stream.Disposed);
	}

	[Test]
	public async Task SilentSnapshotBootstrapAlsoMeetsTheDeadline()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway
		{
			Snapshot = (_, ct) => new TaskCompletionSource<TInvestMarketSnapshot>().Task.WaitAsync(ct)
		};
		using var hub = NewHub(gateway, clock);
		var task = hub.SubscribeAsync(Symbol(), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default);
		await hub.BarrierAsync();
		await Pump(clock, () => gateway.Streams.Count != 0 && Requests(gateway.Streams[0]).Any(r => r.Kind == TInvestSubscriptionKind.All));
		clock.Advance(TimeSpan.FromSeconds(20) - (clock.GetUtcNow() - DateTimeOffset.UnixEpoch));
		Assert.ThrowsAsync<TInvestRequestException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
		await hub.BarrierAsync();
	}

	[Test]
	public async Task BreakReconnectAndResubscribeRestoreOnlyWithFreshClientSnapshot()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		using var recorder = new TInvestTickRecorder(null, clock);
		using var hub = new TInvestMarketDataHub(gateway, new TInvestOptions(), clock, recorder, () => 0.5);
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;
		clock.Advance(TimeSpan.FromSeconds(2));
		gateway.Streams[0].Emit(new TInvestPing());
		await Pump(clock, () => recorder.Coverage(Symbol().Instrument.Short.Uid, new DateOnly(1970, 1, 1)).Count == 1);
		gateway.Streams[0].Break();
		await Until(() => events.OfType<HubFeedStatus>().Any(s => !s.Available));
		await Pump(clock, () => events.OfType<HubFeedStatus>().Any(s => s.Available));
		Assert.That(gateway.Streams.Count, Is.EqualTo(2));
		Assert.That(gateway.Streams[1].Queries, Is.EqualTo(1));
		Assert.That(events.OfType<HubSnapshot>().Count(), Is.EqualTo(1), "No unsolicited snapshot after feed restoration.");
		var again = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default);
		await Pump(clock, () => again.IsCompleted);
		using var resubscription = await again;
		Assert.That(events.OfType<HubSnapshot>().Count(), Is.EqualTo(2));
		Assert.That(events.OfType<HubFeedStatus>().Select(s => s.Available), Is.EqualTo(new[]
		{
			false,
			true
		}));
		clock.Advance(TimeSpan.FromSeconds(2));
		gateway.Streams[1].Emit(new TInvestPing());
		await Pump(clock, () => recorder.Coverage(Symbol().Instrument.Short.Uid, new DateOnly(1970, 1, 1)).Count == 2);
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task ReconnectDoesNotFetchUnaryOrderBookWithoutDepthSubscribers(bool recordingOnly)
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		using var hub = NewHub(gateway, clock);
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default, recordingOnly: recordingOnly);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;
		Assert.That(gateway.OrderBookRequests, Is.Empty);

		gateway.Streams[0].Break();
		await Pump(clock, () => gateway.Streams.Count == 2 && events.OfType<HubFeedStatus>().Any(status => status.Available));
		await hub.BarrierAsync();

		Assert.That(gateway.OrderBookRequests, Is.Empty,
			"Reconnect should use the existing market snapshot and stream path, not request a depth-only unary book for a non-depth subscriber.");
	}

	[Test]
	public async Task InconsistentDepthOneBookDoesNotFetchUnaryOrderBookWithoutDepthSubscribers()
	{
		var clock = new ManualTimeProvider();
		var uid = Symbol().Instrument.Short.Uid;
		using var gateway = new ScriptedTInvestGateway();
		using var hub = NewHub(gateway, clock);
		var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		hub.Observed += item =>
		{
			if (item is TInvestOrderBook)
			{
				observed.TrySetResult();
			}
		};
		var task = hub.SubscribeAsync(Symbol(), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;
		gateway.Streams.Single().Emit(new TInvestOrderBook(uid, new(2, 0), 1,
			new[] { new TInvestBookLevel(100, 1), new TInvestBookLevel(100, 2) },
			new[] { new TInvestBookLevel(101, 1) }));
		await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await hub.BarrierAsync();

		Assert.That(gateway.OrderBookRequests, Is.Empty,
			"An inconsistent best-quote book must not trigger the depth-only unary bootstrap when nobody requested market depth.");
	}

	[Test]
	public async Task PingsKeepAQuietStreamAliveAndSilenceBreaksIt()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		using var hub = NewHub(gateway, clock, new TInvestOptions
		{
			StreamSilenceTimeout = TimeSpan.FromSeconds(30)
		});
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;
		var pings = 0;
		hub.Observed += e =>
		{
			if (e is TInvestPing)
			{
				Interlocked.Increment(ref pings);
			}
		};
		for (var i = 1; i <= 4; i++)
		{
			clock.Advance(TimeSpan.FromSeconds(20));
			gateway.Streams[0].Emit(new TInvestPing());
			await Until(() => pings == i);
		}

		Assert.That(events.OfType<HubFeedStatus>(), Is.Empty);
		clock.Advance(TimeSpan.FromSeconds(31));
		await Until(() => events.OfType<HubFeedStatus>().Any(s => !s.Available));
	}

	[Test]
	public async Task DealerFilterBookDedupAndCurrentSnapshotAreShared()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (uid, depth, _) => Task.FromResult(new TInvestOrderBook(uid, new(5, 0), depth,
				new[] { new TInvestBookLevel(12.4, 2) }, new[] { new TInvestBookLevel(12.8, 3) }))
		};
		using var recorder = new TInvestTickRecorder(null, clock);
		using var hub = new TInvestMarketDataHub(gateway, new TInvestOptions(), clock, recorder);
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;
		var stream = gateway.Streams[0];
		var uid = Symbol().Instrument.Short.Uid;
		stream.Emit(new TInvestTrade(uid, new(1, 123456789), 12.5, 2, TInvestDirection.Buy, false));
		stream.Emit(new TInvestTrade(uid, new(1, 123456790), 12.6, 3, TInvestDirection.Sell, true));
		stream.Emit(new TInvestOrderBook(uid, new(2, 0), 1, new[] { new TInvestBookLevel(12.4, 2) }, new[] { new TInvestBookLevel(12.8, 3) }));
		stream.Emit(new TInvestOrderBook(uid, new(3, 0), 1, new[] { new TInvestBookLevel(12.4, 2) }, new[] { new TInvestBookLevel(12.8, 3) }));
		stream.Emit(new TInvestOrderBook(uid, new(1, 0), 1, new[] { new TInvestBookLevel(11, 1) }, new[] { new TInvestBookLevel(13, 1) }));
		stream.Emit(new TInvestOrderBook(uid, new(4, 0), 1, new[] { new TInvestBookLevel(11, 1) }, new[] { new TInvestBookLevel(13, 1) }, false));
		stream.Emit(new TInvestStatus(uid, TInvestTradingStatus.NormalTrading));
		await Pump(clock, () => events.OfType<HubUpdate>().Any(e => e.Update is TInvestStatus));
		Assert.That(gateway.OrderBookRequests, Is.Empty);
		Assert.That(events.OfType<HubUpdate>().Count(e => e.Update is TInvestTrade), Is.EqualTo(1));
		Assert.That(events.OfType<HubUpdate>().Count(e => e.Update is TInvestQuote), Is.EqualTo(1));
		Assert.That(recorder.RecordedTrades, Is.EqualTo(1));
		HubSnapshot? snapshot = null;
		using var next = await hub.SubscribeAsync(Symbol(), e => snapshot = e as HubSnapshot, clock.GetUtcNow().AddSeconds(20), default, true);
		Assert.That(snapshot!.Snapshot.LastPrice, Is.EqualTo(12.6));
		Assert.That(snapshot.Snapshot.Quote!.Bid, Is.EqualTo(12.4));
		Assert.That(snapshot.Snapshot.Status, Is.EqualTo(TInvestTradingStatus.NormalTrading));
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task DealerOptionChangesDeliveryButRecorderStaysExchangeOnly(bool include)
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		using var recorder = new TInvestTickRecorder(null, clock);
		using var hub = new TInvestMarketDataHub(gateway, new TInvestOptions
		{
			IncludeDealerTrades = include
		}, clock, recorder);
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;
		gateway.Streams[0].Emit(new TInvestTrade(Symbol().Instrument.Short.Uid, new(1, 0), 12, 1, TInvestDirection.Buy, false));
		gateway.Streams[0].Emit(new TInvestStatus(Symbol().Instrument.Short.Uid, TInvestTradingStatus.SessionClose));
		await Until(() => events.OfType<HubUpdate>().Any(e => e.Update is TInvestStatus));
		Assert.That(events.OfType<HubUpdate>().Count(e => e.Update is TInvestTrade), Is.EqualTo(include ? 1 : 0));
		Assert.That(recorder.RecordedTrades, Is.Zero);
	}

	[Test]
	public async Task QuietDepthSubscriptionCompletesFromRoundedUnaryBookAndIgnoresOlderStreamBooks()
	{
		var clock = new ManualTimeProvider();
		var bids = Enumerable.Range(0, 12).Select(i => new TInvestBookLevel(100 - i, i + 1)).ToArray();
		var asks = Enumerable.Range(0, 12).Select(i => new TInvestBookLevel(101 + i, i + 2)).ToArray();
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (uid, depth, _) => Task.FromResult(new TInvestOrderBook(uid, new(100, 123_456_789), depth, bids, asks))
		};
		using var hub = NewHub(gateway, clock);
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 7);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;

		var initial = events.OfType<HubDepthSnapshot>().Single();
		Assert.That(initial.Levels, Is.EqualTo(7));
		Assert.That(initial.Book.Bids.Count, Is.EqualTo(7));
		Assert.That(initial.Book.Asks.Count, Is.EqualTo(7));
		Assert.That(gateway.OrderBookRequests.Single().Depth, Is.EqualTo(10));
		Assert.That(Requests(gateway.Streams.Single()).Where(r => r.Kind == TInvestSubscriptionKind.Book && r.Subscribe).Select(r => r.Depth), Does.Contain(10));

		var observed = 0;
		hub.Observed += item =>
		{
			if (item is TInvestOrderBook)
			{
				Interlocked.Increment(ref observed);
			}
		};
		gateway.Streams.Single().Emit(new TInvestOrderBook(Symbol().Instrument.Short.Uid, new(100, 123_456_788), 10, bids, asks));
		await Until(() => Volatile.Read(ref observed) == 1);
		await hub.BarrierAsync();
		Assert.That(events.OfType<HubDepthUpdate>(), Is.Empty, "A stream book older than the unary orderbook timestamp must not regress the client book.");

		var changedBids = bids.Select((level, index) => index == 0 ? level with { Lots = 99 } : level).ToArray();
		gateway.Streams.Single().Emit(new TInvestOrderBook(Symbol().Instrument.Short.Uid, new(101, 0), 10, changedBids, asks));
		await Until(() => Volatile.Read(ref observed) == 2 && events.OfType<HubDepthUpdate>().Any());
		Assert.That(events.OfType<HubDepthUpdate>().Single().Book.Bids[0].Lots, Is.EqualTo(99));
	}

	[Test]
	public async Task RetriesFailedUnaryBookBootstrapWithinTheSubscriptionDeadline()
	{
		var clock = new ManualTimeProvider();
		var uid = Symbol().Instrument.Short.Uid;
		var calls = 0;
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (id, depth, _) => Interlocked.Increment(ref calls) == 1
				? Task.FromException<TInvestOrderBook>(new TInvestRequestException("transient source failure"))
				: Task.FromResult(new TInvestOrderBook(id, new(100, 0), depth, new[] { new TInvestBookLevel(100, 2) }, new[] { new TInvestBookLevel(101, 3) }))
		};
		using var hub = NewHub(gateway, clock);
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 1);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;

		Assert.That(calls, Is.EqualTo(2));
		Assert.That(events.OfType<HubDepthSnapshot>().Single().Book.Bids.Single().Price, Is.EqualTo(100));
	}

	[Test]
	public async Task InconsistentBookIsSuppressedAndNextConsistentUnaryBookResynchronizesWithSnapshot()
	{
		var clock = new ManualTimeProvider();
		var uid = Symbol().Instrument.Short.Uid;
		var initial = new TInvestOrderBook(uid, new(100, 0), 10, new[] { new TInvestBookLevel(100, 2) }, new[] { new TInvestBookLevel(101, 3) });
		var calls = 0;
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (id, depth, _) =>
			{
				var call = Interlocked.Increment(ref calls);
				return Task.FromResult(initial with { Depth = depth, Time = call == 1 ? initial.Time : new TInvestTimestamp(102, 0), Bids = call == 1 ? initial.Bids : new[] { new TInvestBookLevel(103, 9) } });
			}
		};
		using var hub = NewHub(gateway, clock);
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 5);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;

		var observed = 0;
		hub.Observed += item =>
		{
			if (item is TInvestOrderBook)
			{
				Interlocked.Increment(ref observed);
			}
		};
		gateway.Streams.Single().Emit(initial with { Time = new(101, 0), IsConsistent = false, Bids = new[] { new TInvestBookLevel(102, 8) } });
		await Until(() => Volatile.Read(ref observed) == 1);
		await Pump(clock, () => events.OfType<HubDepthSnapshot>().Count() == 2);

		Assert.That(events.OfType<HubDepthUpdate>(), Is.Empty);
		Assert.That(events.OfType<HubDepthSnapshot>().Last().Book.Bids.Single().Price, Is.EqualTo(103));
		Assert.That(calls, Is.EqualTo(2));
	}

	[Test]
	public async Task InconsistentBookIsSuppressedAndNextConsistentBookResynchronizesWithSnapshot()
	{
		var clock = new ManualTimeProvider();
		var uid = Symbol().Instrument.Short.Uid;
		var initial = new TInvestOrderBook(uid, new(100, 0), 10, new[] { new TInvestBookLevel(100, 2) }, new[] { new TInvestBookLevel(101, 3) });
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (id, depth, _) => Task.FromResult(initial with { Depth = depth })
		};
		using var hub = NewHub(gateway, clock);
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 5);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;

		var observed = 0;
		hub.Observed += item =>
		{
			if (item is TInvestOrderBook)
			{
				Interlocked.Increment(ref observed);
			}
		};
		gateway.Streams.Single().Emit(initial with { Time = new(101, 0), IsConsistent = false, Bids = new[] { new TInvestBookLevel(102, 8) } });
		await Until(() => Volatile.Read(ref observed) == 1);
		await hub.BarrierAsync();
		Assert.That(events.OfType<HubDepthSnapshot>().Count(), Is.EqualTo(1));
		Assert.That(events.OfType<HubDepthUpdate>(), Is.Empty);

		gateway.Streams.Single().Emit(initial with { Time = new(102, 0), Bids = new[] { new TInvestBookLevel(103, 9) } });
		await Until(() => Volatile.Read(ref observed) == 2 && events.OfType<HubDepthSnapshot>().Count() == 2);
		Assert.That(events.OfType<HubDepthUpdate>(), Is.Empty);
		Assert.That(events.OfType<HubDepthSnapshot>().Last().Book.Bids.Single().Price, Is.EqualTo(103));
	}

	[Test]
	public async Task UnaryBookWithInsufficientDepthMetadataIsRetriedButSparseBookIsAccepted()
	{
		var clock = new ManualTimeProvider();
		var calls = 0;
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (uid, depth, _) =>
			{
				var call = Interlocked.Increment(ref calls);
				var responseDepth = call == 1 ? 10 : depth;
				var count = call == 1 ? 10 : 1;
				var bids = Enumerable.Range(0, count).Select(i => new TInvestBookLevel(100 - i, i + 1)).ToArray();
				var asks = Enumerable.Range(0, count).Select(i => new TInvestBookLevel(101 + i, i + 1)).ToArray();
				return Task.FromResult(new TInvestOrderBook(uid, new(100 + call, 0), responseDepth, bids, asks));
			}
		};
		using var hub = NewHub(gateway, clock);
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 15);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;

		var snapshot = events.OfType<HubDepthSnapshot>().Single();
		Assert.That(calls, Is.EqualTo(2));
		Assert.That(gateway.OrderBookRequests.Select(request => request.Depth), Is.EqualTo(new[] { 20, 20 }));
		Assert.That(snapshot.Book.Depth, Is.EqualTo(20));
		Assert.That(snapshot.Book.Bids, Has.Count.EqualTo(1), "A valid sparse book may contain fewer entries than its requested depth.");
		Assert.That(snapshot.Book.Asks, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task UnknownDepthStreamBookTriggersSnapshotInsteadOfPublishingShallowReplacement()
	{
		var clock = new ManualTimeProvider();
		var uid = Symbol().Instrument.Short.Uid;
		var bids = Enumerable.Range(0, 10).Select(i => new TInvestBookLevel(100 - i, i + 1)).ToArray();
		var asks = Enumerable.Range(0, 10).Select(i => new TInvestBookLevel(101 + i, i + 1)).ToArray();
		var calls = 0;
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (id, depth, _) =>
			{
				var call = Interlocked.Increment(ref calls);
				return Task.FromResult(new TInvestOrderBook(id, new(100 + call, 0), depth, bids, asks));
			}
		};
		using var hub = NewHub(gateway, clock);
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 5);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;

		var observed = 0;
		hub.Observed += item =>
		{
			if (item is TInvestOrderBook)
			{
				Interlocked.Increment(ref observed);
			}
		};
		gateway.Streams.Single().Emit(new TInvestOrderBook(uid, new(101, 0), 0,
			new[] { bids[0] with { Lots = 99 } }, new[] { asks[0] }));
		await Pump(clock, () => events.OfType<HubDepthSnapshot>().Count() == 2 || events.OfType<HubDepthUpdate>().Any());

		Assert.That(Volatile.Read(ref observed), Is.EqualTo(1));
		Assert.That(events.OfType<HubDepthUpdate>(), Is.Empty);
		Assert.That(events.OfType<HubDepthSnapshot>().Last().Book.Bids, Has.Count.EqualTo(5));
		Assert.That(calls, Is.EqualTo(2));
	}

	[Test]
	public async Task RepeatedInconsistentBooksShareOneBootstrapAndHonorLatestFence()
	{
		var clock = new ManualTimeProvider();
		var uid = Symbol().Instrument.Short.Uid;
		var initial = new TInvestOrderBook(uid, new(100, 0), 10,
			new[] { new TInvestBookLevel(100, 2) }, new[] { new TInvestBookLevel(101, 3) });
		var bootstrap = new TaskCompletionSource<TInvestOrderBook>(TaskCreationOptions.RunContinuationsAsynchronously);
		var calls = 0;
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (id, depth, _) => Interlocked.Increment(ref calls) == 1
				? Task.FromResult(initial with { Depth = depth })
				: bootstrap.Task
		};
		using var hub = NewHub(gateway, clock);
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 5);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;

		var observed = 0;
		hub.Observed += item =>
		{
			if (item is TInvestOrderBook)
			{
				Interlocked.Increment(ref observed);
			}
		};
		var stream = gateway.Streams.Single();
		stream.Emit(initial with { Time = new(101, 0), IsConsistent = false });
		await Pump(clock, () => Volatile.Read(ref observed) == 1 && Volatile.Read(ref calls) == 2);
		stream.Emit(initial with { Time = new(102, 0), IsConsistent = false });
		stream.Emit(initial with { Time = new(103, 0), IsConsistent = false });
		await Pump(clock, () => Volatile.Read(ref observed) == 3);

		Assert.That(calls, Is.EqualTo(2), "One in-flight unary bootstrap covers repeated inconsistent stream messages.");
		bootstrap.SetResult(initial with { Time = new(104, 0), Bids = new[] { new TInvestBookLevel(103, 9) } });
		await Pump(clock, () => events.OfType<HubDepthSnapshot>().Count() == 2);

		Assert.That(events.OfType<HubDepthUpdate>(), Is.Empty);
		Assert.That(events.OfType<HubDepthSnapshot>().Last().Book.Time, Is.EqualTo(new TInvestTimestamp(104, 0)));
	}

	[Test]
	public async Task QuietLowerDepthIntervalRequiresFreshSnapshotWhenDepthIsExpandedAgain()
	{
		var clock = new ManualTimeProvider();
		var calls = 0;
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (uid, depth, _) =>
			{
				var call = Interlocked.Increment(ref calls);
				var count = call == 1 ? depth : 1;
				var bids = Enumerable.Range(0, count).Select(i => new TInvestBookLevel(100 - i, i + 1)).ToArray();
				var asks = Enumerable.Range(0, count).Select(i => new TInvestBookLevel(101 + i, i + 1)).ToArray();
				return Task.FromResult(new TInvestOrderBook(uid, new(100 + call, 0), depth, bids, asks));
			}
		};
		using var hub = NewHub(gateway, clock);
		var quotes = new ConcurrentQueue<HubEvent>();
		var quoteTask = hub.SubscribeAsync(Symbol(), quotes.Enqueue, clock.GetUtcNow().AddSeconds(20), default);
		await Pump(clock, () => quoteTask.IsCompleted);
		using var quoteSubscription = await quoteTask;

		var deepTask = hub.SubscribeAsync(Symbol(), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 15);
		await Pump(clock, () => deepTask.IsCompleted);
		using var deepSubscription = await deepTask;
		Assert.That(gateway.OrderBookRequests.Select(request => request.Depth), Is.EqualTo(new[] { 20 }));

		deepSubscription.Dispose();
		await Pump(clock, () => Requests(gateway.Streams.Single()).Any(request => request.Kind == TInvestSubscriptionKind.Book && !request.Subscribe && request.Depth == 20) &&
			Requests(gateway.Streams.Single()).Any(request => request.Kind == TInvestSubscriptionKind.Book && request.Subscribe && request.Depth == 1));
		var reexpandedEvents = new ConcurrentQueue<HubEvent>();
		var reexpandedTask = hub.SubscribeAsync(Symbol(), reexpandedEvents.Enqueue, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 7);
		await Pump(clock, () => reexpandedTask.IsCompleted);
		using var reexpandedSubscription = await reexpandedTask;

		Assert.That(gateway.OrderBookRequests.Select(request => request.Depth), Is.EqualTo(new[] { 20, 10 }));
		Assert.That(reexpandedEvents.OfType<HubDepthSnapshot>().Single().Book.Depth, Is.EqualTo(10));
		Assert.That(reexpandedEvents.OfType<HubDepthSnapshot>().Single().Book.Bids, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task DeeperBookHandoverReplacesDepthOneSubscriptionAndKeepsBestQuoteCurrent()
	{
		var clock = new ManualTimeProvider();
		var uid = Symbol().Instrument.Short.Uid;
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (id, depth, _) => Task.FromResult(new TInvestOrderBook(id, new(101, 0), depth,
				Enumerable.Range(0, depth).Select(i => new TInvestBookLevel(102 - i, i + 1)).ToArray(),
				Enumerable.Range(0, depth).Select(i => new TInvestBookLevel(103 + i, i + 1)).ToArray()))
		};
		using var hub = NewHub(gateway, clock);
		var quoteEvents = new ConcurrentQueue<HubEvent>();
		var quoteTask = hub.SubscribeAsync(Symbol(), quoteEvents.Enqueue, clock.GetUtcNow().AddSeconds(20), default);
		await Pump(clock, () => quoteTask.IsCompleted);
		using var quoteSubscription = await quoteTask;
		var stream = gateway.Streams.Single();
		stream.Emit(new TInvestOrderBook(uid, new(100, 0), 1, new[] { new TInvestBookLevel(100, 2) }, new[] { new TInvestBookLevel(101, 3) }));
		await Until(() => quoteEvents.OfType<HubUpdate>().Any());

		var depthEvents = new ConcurrentQueue<HubEvent>();
		var depthTask = hub.SubscribeAsync(Symbol(), depthEvents.Enqueue, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 15);
		await Pump(clock, () => depthTask.IsCompleted);
		using var depthSubscription = await depthTask;

		Assert.That(gateway.OrderBookRequests.Select(request => request.Depth), Is.EqualTo(new[] { 20 }));
		Assert.That(depthEvents.OfType<HubDepthSnapshot>().Single().Book.Bids[0].Price, Is.EqualTo(102));
		Assert.That(quoteEvents.OfType<HubUpdate>().Count(), Is.EqualTo(2));
		Assert.That(((TInvestQuote)quoteEvents.OfType<HubUpdate>().Last().Update).Bid, Is.EqualTo(102));
		var requests = Requests(stream).Where(request => request.Kind == TInvestSubscriptionKind.Book).ToArray();
		Assert.That(requests.Any(request => request.Subscribe && request.Depth == 1), Is.True);
		await Pump(clock, () => Requests(stream).Any(request => request.Kind == TInvestSubscriptionKind.Book && !request.Subscribe && request.Depth == 1) && Requests(stream).Any(request => request.Kind == TInvestSubscriptionKind.Book && request.Subscribe && request.Depth == 20));
		requests = Requests(stream).Where(request => request.Kind == TInvestSubscriptionKind.Book).ToArray();
		Assert.That(requests.Any(request => !request.Subscribe && request.Depth == 1), Is.True);
		Assert.That(requests.Any(request => request.Subscribe && request.Depth == 20), Is.True);
		var requestsBeforeDowngrade = Requests(stream).Count(request => request.Kind == TInvestSubscriptionKind.Book);
		depthSubscription.Dispose();
		await Pump(clock, () => Requests(stream).Where(request => request.Kind == TInvestSubscriptionKind.Book).Skip(requestsBeforeDowngrade)
			.Any(request => request.Subscribe && request.Depth == 1) &&
			Requests(stream).Where(request => request.Kind == TInvestSubscriptionKind.Book).Skip(requestsBeforeDowngrade)
			.Any(request => !request.Subscribe && request.Depth == 20));
		var downgradeRequests = Requests(stream).Where(request => request.Kind == TInvestSubscriptionKind.Book).Skip(requestsBeforeDowngrade).ToArray();
		var unsubscribeDepthTwenty = Array.FindIndex(downgradeRequests, request => !request.Subscribe && request.Depth == 20);
		var subscribeDepthOne = Array.FindIndex(downgradeRequests, request => request.Subscribe && request.Depth == 1);
		Assert.That(unsubscribeDepthTwenty, Is.GreaterThanOrEqualTo(0));
		Assert.That(subscribeDepthOne, Is.GreaterThan(unsubscribeDepthTwenty), "Release the old subscription before restoring the best-quote depth.");

		stream.Emit(new TInvestOrderBook(uid, new(102, 0), 1, new[] { new TInvestBookLevel(104, 4) }, new[] { new TInvestBookLevel(105, 5) }));
		await Until(() => quoteEvents.OfType<HubUpdate>().Count() == 3);
		Assert.That(((TInvestQuote)quoteEvents.OfType<HubUpdate>().Last().Update).Bid, Is.EqualTo(104));
	}

	[Test]
	public async Task SnapshotOnlyDepthRequiresAConsistentUnaryBookAndDoesNotOpenAStream()
	{
		var clock = new ManualTimeProvider();
		var uid = Symbol().Instrument.Short.Uid;
		var calls = 0;
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (id, depth, _) =>
			{
				var call = Interlocked.Increment(ref calls);
				var bids = call == 1
					? new[] { new TInvestBookLevel(100, 1), new TInvestBookLevel(100, 2) }
					: new[] { new TInvestBookLevel(100, 1) };
				return Task.FromResult(new TInvestOrderBook(id, new(100 + call, 0), depth, bids, new[] { new TInvestBookLevel(101, 1) }));
			}
		};
		using var hub = NewHub(gateway, clock);
		HubDepthSnapshot? result = null;
		var task = hub.SubscribeAsync(Symbol(), item => result = item as HubDepthSnapshot,
			clock.GetUtcNow().AddSeconds(20), default, snapshotOnly: true, depthLevels: 5);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;

		Assert.That(calls, Is.EqualTo(2));
		Assert.That(result!.Levels, Is.EqualTo(5));
		Assert.That(result.Book.Bids.Count, Is.EqualTo(1));
		Assert.That(gateway.OrderBookRequests.Select(request => request.Depth), Is.All.EqualTo(10));
		Assert.That(gateway.Streams, Is.Empty, "Snapshot-only depth must not create an upstream stream.");
	}

	[Test]
	public async Task FailedDepthUpgradeRejectsOnlyTheUnservedSubscriberAndRetainsTheActiveBook()
	{
		var clock = new ManualTimeProvider();
		var uid = Symbol().Instrument.Short.Uid;
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (id, depth, _) => Task.FromResult(new TInvestOrderBook(id, new(100, 0), depth,
				Enumerable.Range(0, depth).Select(i => new TInvestBookLevel(100 - i, i + 1)).ToArray(),
				Enumerable.Range(0, depth).Select(i => new TInvestBookLevel(101 + i, i + 1)).ToArray()))
		};
		using var hub = NewHub(gateway, clock);
		var lowEvents = new ConcurrentQueue<HubEvent>();
		var lowTask = hub.SubscribeAsync(Symbol(), lowEvents.Enqueue, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 7);
		await Pump(clock, () => lowTask.IsCompleted);
		using var low = await lowTask;
		var stream = gateway.Streams.Single();
		stream.RequestStatus = (request, _) => request.Kind == TInvestSubscriptionKind.Book && request.Subscribe && request.Depth == 20
			? TInvestSubscriptionStatus.LimitIsExceeded
			: TInvestSubscriptionStatus.Success;

		var highTask = hub.SubscribeAsync(Symbol(), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 15);
		await Pump(clock, () => highTask.IsCompleted);
		var error = Assert.ThrowsAsync<TInvestRequestException>(async () => await highTask);
		Assert.That(error!.Message, Does.Contain("subscription limit"));
		await hub.BarrierAsync();

		stream.Emit(new TInvestOrderBook(uid, new(101, 0), 10,
			Enumerable.Range(0, 10).Select(i => new TInvestBookLevel(200 - i, i + 1)).ToArray(),
			Enumerable.Range(0, 10).Select(i => new TInvestBookLevel(201 + i, i + 1)).ToArray()));
		await Until(() => lowEvents.OfType<HubDepthUpdate>().Any());
		Assert.That(lowEvents.OfType<HubFeedStatus>(), Is.Empty, "A failed depth increase must not report the still-available old feed as down.");
		Assert.That(lowEvents.OfType<HubDepthSnapshot>().Count(), Is.EqualTo(1));
		Assert.That(lowEvents.OfType<HubDepthUpdate>().Single().Book.Bids[0].Price, Is.EqualTo(200));
	}

	[Test]
	public async Task RejectedDepthDowngradeMarksRemainingSubscribersUnavailable()
	{
		var clock = new ManualTimeProvider();
		var uid = Symbol().Instrument.Short.Uid;
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (id, depth, _) => Task.FromResult(new TInvestOrderBook(id, new(100 + depth, 0), depth,
				Enumerable.Range(0, depth).Select(i => new TInvestBookLevel(100 - i, i + 1)).ToArray(),
				Enumerable.Range(0, depth).Select(i => new TInvestBookLevel(101 + i, i + 1)).ToArray()))
		};
		using var hub = NewHub(gateway, clock);
		var lowEvents = new ConcurrentQueue<HubEvent>();
		var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var lowTask = hub.SubscribeAsync(Symbol(), item =>
		{
			lowEvents.Enqueue(item);
			if (item is HubFeedStatus { Available: false })
			{
				unavailable.TrySetResult();
			}
		}, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 7);
		await Pump(clock, () => lowTask.IsCompleted);
		using var low = await lowTask;
		var highTask = hub.SubscribeAsync(Symbol(), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 15);
		await Pump(clock, () => highTask.IsCompleted);
		using var high = await highTask;
		var stream = gateway.Streams.Single();
		var previousShallowSubscriptions = Requests(stream).Count(request => request.Kind == TInvestSubscriptionKind.Book && request.Subscribe && request.Depth == 10);
		stream.RequestStatus = (request, _) => request.Kind == TInvestSubscriptionKind.Book && request.Subscribe && request.Depth == 10
			? TInvestSubscriptionStatus.TooManyRequests
			: TInvestSubscriptionStatus.Success;

		high.Dispose();
		await Pump(clock, () => Requests(stream).Count(request => request.Kind == TInvestSubscriptionKind.Book && request.Subscribe && request.Depth == 10) > previousShallowSubscriptions);
		await unavailable.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await hub.BarrierAsync();

		Assert.That(lowEvents.OfType<HubFeedStatus>().Any(status => !status.Available), Is.True,
			"When the deeper book is removed and its lower-depth replacement is rejected, the remaining depth subscriber must be told the book feed is unavailable.");
	}

	[Test]
	public async Task DuplicateBookTriggersBoundedRetryThenResynchronizesActiveSubscribers()
	{
		var clock = new ManualTimeProvider();
		var uid = Symbol().Instrument.Short.Uid;
		var calls = 0;
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (id, depth, _) =>
			{
				var call = Interlocked.Increment(ref calls);
				var bidLevels = call is >= 2 and <= 4
					? new[] { new TInvestBookLevel(102, 2), new TInvestBookLevel(102, 3) }
					: new[] { new TInvestBookLevel(call == 1 ? 100 : 103, 2) };
				return Task.FromResult(new TInvestOrderBook(id, new(call == 1 ? 100 : 102, 0), depth, bidLevels, new[] { new TInvestBookLevel(101, 3) }));
			}
		};
		using var hub = NewHub(gateway, clock);
		var events = new ConcurrentQueue<HubEvent>();
		var task = hub.SubscribeAsync(Symbol(), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 5);
		await Pump(clock, () => task.IsCompleted);
		using var subscription = await task;

		var duplicate = new TInvestOrderBook(uid, new(101, 0), 10,
			new[] { new TInvestBookLevel(102, 2), new TInvestBookLevel(102, 3) },
			new[] { new TInvestBookLevel(103, 1) });
		gateway.Streams.Single().Emit(duplicate);
		await Pump(clock, () => events.OfType<HubDepthSnapshot>().Count() == 2);

		Assert.That(calls, Is.EqualTo(5), "Three inconsistent unary responses are retried, then a delayed retry repairs the live book.");
		Assert.That(events.OfType<HubDepthUpdate>(), Is.Empty);
		Assert.That(events.OfType<HubDepthSnapshot>().Last().Book.Bids.Single().Price, Is.EqualTo(103));
	}

	[Test]
	public async Task SharedBookSubscriptionTracksHighestRoundedDepthDemand()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway
		{
			OrderBook = (uid, depth, _) => Task.FromResult(new TInvestOrderBook(uid, new(100 + depth, 0), depth,
				Enumerable.Range(0, depth).Select(i => new TInvestBookLevel((depth == 20 ? 200 : 100) - i, i + 1)).ToArray(),
				Enumerable.Range(0, depth).Select(i => new TInvestBookLevel((depth == 20 ? 201 : 101) + i, i + 1)).ToArray()))
		};
		using var hub = NewHub(gateway, clock);
		var lowEvents = new ConcurrentQueue<HubEvent>();
		var lowTask = hub.SubscribeAsync(Symbol(), lowEvents.Enqueue, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 7);
		await Pump(clock, () => lowTask.IsCompleted);
		using var low = await lowTask;
		var highTask = hub.SubscribeAsync(Symbol(), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default, depthLevels: 15);
		await Pump(clock, () => highTask.IsCompleted);
		using var high = await highTask;

		Assert.That(gateway.OrderBookRequests.Select(request => request.Depth), Is.EqualTo(new[] { 10, 20 }));
		await Pump(clock, () => Requests(gateway.Streams.Single()).Any(request => request.Kind == TInvestSubscriptionKind.Book && !request.Subscribe && request.Depth == 10) && Requests(gateway.Streams.Single()).Any(request => request.Kind == TInvestSubscriptionKind.Book && request.Subscribe && request.Depth == 20));
		var bookRequests = Requests(gateway.Streams.Single()).Where(request => request.Kind == TInvestSubscriptionKind.Book).ToArray();
		Assert.That(bookRequests.Any(request => request.Subscribe && request.Depth == 10), Is.True);
		Assert.That(bookRequests.Any(request => !request.Subscribe && request.Depth == 10), Is.True);
		Assert.That(bookRequests.Any(request => request.Subscribe && request.Depth == 20), Is.True);
		Assert.That(lowEvents.OfType<HubDepthSnapshot>().Count(), Is.EqualTo(1));
		Assert.That(lowEvents.OfType<HubDepthUpdate>().Count(), Is.EqualTo(1), "A successful handover sends a delta to existing lower-depth clients, not an unsolicited full snapshot.");
		Assert.That(lowEvents.OfType<HubDepthUpdate>().Single().Book.Bids[0].Price, Is.EqualTo(200));

		var requestsBeforeDowngrade = Requests(gateway.Streams.Single()).Count(request => request.Kind == TInvestSubscriptionKind.Book);
		high.Dispose();
		await Pump(clock, () => Requests(gateway.Streams.Single()).Where(request => request.Kind == TInvestSubscriptionKind.Book).Skip(requestsBeforeDowngrade)
			.Any(request => request.Subscribe && request.Depth == 10) &&
			Requests(gateway.Streams.Single()).Where(request => request.Kind == TInvestSubscriptionKind.Book).Skip(requestsBeforeDowngrade)
			.Any(request => !request.Subscribe && request.Depth == 20));
		var downgradeRequests = Requests(gateway.Streams.Single()).Where(request => request.Kind == TInvestSubscriptionKind.Book).Skip(requestsBeforeDowngrade).ToArray();
		var unsubscribeHigh = Array.FindIndex(downgradeRequests, request => !request.Subscribe && request.Depth == 20);
		var resubscribeLow = Array.FindIndex(downgradeRequests, request => request.Subscribe && request.Depth == 10);
		Assert.That(unsubscribeHigh, Is.GreaterThanOrEqualTo(0));
		Assert.That(resubscribeLow, Is.GreaterThan(unsubscribeHigh), "Unsubscribe the deeper book before restoring depth 10.");
	}

	internal static async Task Until(Func<bool> condition)
	{
		var timeout = System.Diagnostics.Stopwatch.StartNew();
		while (!condition())
		{
			if (timeout.Elapsed > TimeSpan.FromSeconds(10))
			{
				Assert.Fail("Scripted worker did not settle.");
			}

			await Task.Yield();
		}
	}

	internal static async Task Pump(ManualTimeProvider clock, Func<bool> condition)
	{
		var wall = System.Diagnostics.Stopwatch.StartNew();
		var nextAdvance = TimeSpan.Zero;
		while (!condition())
		{
			if (wall.Elapsed > TimeSpan.FromSeconds(10))
			{
				Assert.Fail("Scripted clock driver did not settle.");
			}

			await Task.Yield();
			// Give the worker a chance to install its next fake timer; this is harness scheduling, not a real-time
			// timing assertion. Every timeout/backoff under test still advances only on ManualTimeProvider.
			if (wall.Elapsed < nextAdvance)
			{
				continue;
			}

			clock.Advance(TimeSpan.FromMilliseconds(20));
			nextAdvance = wall.Elapsed + TimeSpan.FromMilliseconds(5);
		}
	}

	static TInvestSymbol Symbol(string ticker = "SYNTH") => new(ScriptedTInvestGateway.Invent(ticker));
	static TInvestMarketDataHub NewHub(ScriptedTInvestGateway gateway, ManualTimeProvider clock, TInvestOptions? options = null) => new(gateway, options ?? new TInvestOptions(), clock, new TInvestTickRecorder(null, clock), () => 0.5);
	static TInvestStreamRequest[] Requests(ScriptedMarketDataStream stream)
	{
		lock (stream.Requests)
			return stream.Requests.ToArray();
	}
}
