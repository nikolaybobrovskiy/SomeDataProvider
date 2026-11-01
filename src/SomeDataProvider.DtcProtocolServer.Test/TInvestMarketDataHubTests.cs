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
		using var gateway = new ScriptedTInvestGateway();
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
		stream.Emit(new TInvestQuote(uid, new(2, 0), 12.4, 2, 12.8, 3));
		stream.Emit(new TInvestQuote(uid, new(3, 0), 12.4, 2, 12.8, 3));
		stream.Emit(new TInvestQuote(uid, new(1, 0), 11, 1, 13, 1));
		stream.Emit(new TInvestQuote(uid, new(4, 0), 11, 1, 13, 1, false));
		stream.Emit(new TInvestStatus(uid, TInvestTradingStatus.NormalTrading));
		await Until(() => events.OfType<HubUpdate>().Any(e => e.Update is TInvestStatus));
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
