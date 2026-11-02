namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Market;

[TestFixture]
public class TInvestStreamLifecycleTests
{
	[TestCase(false)]
	[TestCase(true)]
	public async Task SubscriptionReconciliationRetriesOnceAndLeavesPersistentMismatchUnavailable(bool missingAgain)
	{
		var clock = new ManualTimeProvider();
		var stream = new ScriptedMarketDataStream();
		var uid = ScriptedTInvestGateway.Invent().Short.Uid;
		stream.Sending = (request, self) =>
		{
			if (request.Kind == TInvestSubscriptionKind.All && (missingAgain || self.Queries == 0))
			{
				self.Forget(TInvestSubscriptionKind.Book, uid);
			}
		};
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ => Task.FromResult<ITInvestMarketDataStream>(stream)
		};
		var statuses = new ConcurrentQueue<(string Uid, bool Available)>();
		using var upstream = new UpstreamStream(gateway, clock, TimeSpan.FromSeconds(180), _ =>
		{
		}, (id, available) => statuses.Enqueue((id, available)), (_, _) =>
		{
		}, () => 0.5);
		upstream.SetDesired(new[]
		{
			uid,
			"invented-other"
		});
		await TInvestMarketDataHubTests.Pump(clock, () => statuses.Any(s => s.Uid == "invented-other" && s.Available));
		clock.Advance(TimeSpan.FromMilliseconds(100));
		await TInvestMarketDataHubTests.Until(() => stream.Queries == 2);
		Assert.That(statuses.Any(s => s.Uid == uid && s.Available), Is.EqualTo(!missingAgain));
		Assert.That(statuses.Any(s => s.Uid == uid && !s.Available), Is.EqualTo(missingAgain));
		TInvestStreamRequest[] requests;
		lock (stream.Requests)
			requests = stream.Requests.ToArray();
		Assert.That(requests.Count(r => r.Kind == TInvestSubscriptionKind.Book && r.Subscribe && r.Uids.Contains(uid)), Is.EqualTo(2));
		upstream.Dispose();
		await upstream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task BookDepthUpgradeUnsubscribesFirstAndRestoresOldDepthAfterReplacementRejection()
	{
		var clock = new ManualTimeProvider();
		var uid = ScriptedTInvestGateway.Invent().Short.Uid;
		var stream = new ScriptedMarketDataStream();
		var rejectUpgrade = true;
		stream.RequestStatus = (request, _) => request.Kind == TInvestSubscriptionKind.Book && request.Subscribe && request.Depth == 20 && rejectUpgrade
			? TInvestSubscriptionStatus.LimitIsExceeded
			: TInvestSubscriptionStatus.Success;
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ => Task.FromResult<ITInvestMarketDataStream>(stream)
		};
		var ready = new ConcurrentQueue<int>();
		var rejected = new ConcurrentQueue<(TInvestSubscriptionKind Kind, TInvestSubscriptionStatus Status)>();
		using var upstream = new UpstreamStream(gateway, clock, TimeSpan.FromSeconds(180), _ =>
		{
		}, (_, _) =>
		{
		}, (_, _) =>
		{
		}, () => 0.5, rejectedWithKind: (_, kind, status) => rejected.Enqueue((kind, status)), bookReady: (_, depth) => ready.Enqueue(depth));

		upstream.SetDesired(new Dictionary<string, int> { [uid] = 10 });
		await TInvestMarketDataHubTests.Pump(clock, () => ready.Contains(10));
		var readyAtDepthTen = ready.Count(depth => depth == 10);
		upstream.SetDesired(new Dictionary<string, int> { [uid] = 20 });
		await TInvestMarketDataHubTests.Pump(clock, () => rejected.Any(item => item.Kind == TInvestSubscriptionKind.Book && item.Status == TInvestSubscriptionStatus.LimitIsExceeded) &&
			Requests(stream).Count(request => request.Kind == TInvestSubscriptionKind.Book && request.Subscribe && request.Depth == 10) >= 2);

		var requests = Requests(stream).Where(request => request.Kind == TInvestSubscriptionKind.Book && request.Uids.Contains(uid)).ToArray();
		var subscribeOld = Array.FindIndex(requests, request => request.Subscribe && request.Depth == 10);
		var unsubscribeOld = Array.FindIndex(requests, request => !request.Subscribe && request.Depth == 10);
		var rejectedReplacement = Array.FindIndex(requests, request => request.Subscribe && request.Depth == 20);
		var restoredOld = Array.FindLastIndex(requests, request => request.Subscribe && request.Depth == 10);
		Assert.That(subscribeOld, Is.GreaterThanOrEqualTo(0));
		Assert.That(unsubscribeOld, Is.GreaterThan(subscribeOld));
		Assert.That(rejectedReplacement, Is.GreaterThan(unsubscribeOld));
		Assert.That(restoredOld, Is.GreaterThan(rejectedReplacement), "The old depth is restored after the replacement is rejected, while the deeper depth remains desired.");

		upstream.SetDesired(new Dictionary<string, int> { [uid] = 10 });
		await TInvestMarketDataHubTests.Pump(clock, () => ready.Count(depth => depth == 10) > readyAtDepthTen);

		upstream.Dispose();
		await upstream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task FailedOldDepthUnsubscribeDoesNotRejectTheReplacementDepth()
	{
		var clock = new ManualTimeProvider();
		var uid = ScriptedTInvestGateway.Invent().Short.Uid;
		var stream = new ScriptedMarketDataStream
		{
			RequestStatus = (request, _) => request.Kind == TInvestSubscriptionKind.Book && !request.Subscribe && request.Depth == 10
				? TInvestSubscriptionStatus.InternalError
				: TInvestSubscriptionStatus.Success
		};
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ => Task.FromResult<ITInvestMarketDataStream>(stream)
		};
		var ready = new ConcurrentQueue<int>();
		var rejected = new ConcurrentQueue<(TInvestSubscriptionKind Kind, TInvestSubscriptionStatus Status)>();
		using var upstream = new UpstreamStream(gateway, clock, TimeSpan.FromSeconds(180), _ =>
		{
		}, (_, _) =>
		{
		}, (_, _) =>
		{
		}, () => 0.5, rejectedWithKind: (_, kind, status) => rejected.Enqueue((kind, status)), bookReady: (_, depth) => ready.Enqueue(depth));

		upstream.SetDesired(new Dictionary<string, int> { [uid] = 10 });
		await TInvestMarketDataHubTests.Pump(clock, () => ready.Contains(10));
		upstream.SetDesired(new Dictionary<string, int> { [uid] = 20 });
		clock.Advance(TimeSpan.FromSeconds(30));
		await TInvestMarketDataHubTests.Pump(clock, () => Requests(stream).Count(request => request.Kind == TInvestSubscriptionKind.Book && !request.Subscribe && request.Depth == 10) >= 2);

		Assert.That(Requests(stream).Any(request => request.Kind == TInvestSubscriptionKind.Book && request.Subscribe && request.Depth == 20), Is.False);
		Assert.That(rejected, Is.Empty, "An old-depth unsubscribe failure is not a rejection of the requested replacement depth.");

		upstream.Dispose();
		await upstream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task FullStreamDepthReplacementNeverExceedsThreeHundredCountedSubscriptions()
	{
		var clock = new ManualTimeProvider();
		var stream = new ScriptedMarketDataStream();
		var uids = Enumerable.Range(0, 150).Select(index => $"invented-capacity-{index}").ToArray();
		var active = new HashSet<(TInvestSubscriptionKind Kind, string Uid, int Depth)>();
		var maximumActive = 0;
		stream.Sending = (request, _) =>
		{
			if (request.Kind != TInvestSubscriptionKind.Trades && request.Kind != TInvestSubscriptionKind.Book)
			{
				return;
			}

			lock (active)
			{
				foreach (var id in request.Uids)
				{
					var key = (request.Kind, id, request.Kind == TInvestSubscriptionKind.Book ? request.Depth : 0);
					if (request.Subscribe)
					{
						active.Add(key);
					}
					else
					{
						active.Remove(key);
					}
				}

				maximumActive = Math.Max(maximumActive, active.Count);
			}
		};
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ => Task.FromResult<ITInvestMarketDataStream>(stream)
		};
		var ready = new ConcurrentQueue<(string Uid, int Depth)>();
		using var upstream = new UpstreamStream(gateway, clock, TimeSpan.FromSeconds(180), _ =>
		{
		}, (_, _) =>
		{
		}, (_, _) =>
		{
		}, () => 0.5, bookReady: (id, depth) => ready.Enqueue((id, depth)));

		upstream.SetDesired(uids.ToDictionary(id => id, _ => 1, StringComparer.Ordinal));
		await TInvestMarketDataHubTests.Pump(clock, () => ready.Where(item => item.Depth == 1).Select(item => item.Uid).Distinct().Count() == uids.Length);
		upstream.SetDesired(uids.ToDictionary(id => id, _ => 20, StringComparer.Ordinal));
		await TInvestMarketDataHubTests.Pump(clock, () => ready.Where(item => item.Depth == 20).Select(item => item.Uid).Distinct().Count() == uids.Length);

		Assert.That(maximumActive, Is.LessThanOrEqualTo(300));

		upstream.Dispose();
		await upstream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task RemovingTheDeeperBookUnsubscribesBeforeRestoringDepthOne()
	{
		var clock = new ManualTimeProvider();
		var uid = ScriptedTInvestGateway.Invent().Short.Uid;
		var stream = new ScriptedMarketDataStream();
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ => Task.FromResult<ITInvestMarketDataStream>(stream)
		};
		var ready = new ConcurrentQueue<int>();
		using var upstream = new UpstreamStream(gateway, clock, TimeSpan.FromSeconds(180), _ =>
		{
		}, (_, _) =>
		{
		}, (_, _) =>
		{
		}, () => 0.5, bookReady: (_, depth) => ready.Enqueue(depth));

		upstream.SetDesired(new Dictionary<string, int> { [uid] = 1 });
		await TInvestMarketDataHubTests.Pump(clock, () => ready.Contains(1));
		upstream.SetDesired(new Dictionary<string, int> { [uid] = 20 });
		await TInvestMarketDataHubTests.Pump(clock, () => ready.Contains(20) && Requests(stream).Any(request => request.Kind == TInvestSubscriptionKind.Book && !request.Subscribe && request.Depth == 1));
		var readyAtDepthOne = ready.Count(depth => depth == 1);
		upstream.SetDesired(new Dictionary<string, int> { [uid] = 1 });
		await TInvestMarketDataHubTests.Pump(clock, () => ready.Count(depth => depth == 1) > readyAtDepthOne &&
			Requests(stream).Any(request => request.Kind == TInvestSubscriptionKind.Book && !request.Subscribe && request.Depth == 20) &&
			Requests(stream).Count(request => request.Kind == TInvestSubscriptionKind.Book && request.Subscribe && request.Depth == 1) >= 2);

		var bookRequests = Requests(stream).Where(request => request.Kind == TInvestSubscriptionKind.Book && request.Uids.Contains(uid)).ToArray();
		var subscribeDeep = Array.FindIndex(bookRequests, request => request.Subscribe && request.Depth == 20);
		var unsubscribeShallow = Array.FindIndex(bookRequests, request => !request.Subscribe && request.Depth == 1);
		var subscribeShallowAgain = Array.FindLastIndex(bookRequests, request => request.Subscribe && request.Depth == 1);
		var unsubscribeDeep = Array.FindIndex(bookRequests, request => !request.Subscribe && request.Depth == 20);
		Assert.That(subscribeDeep, Is.GreaterThanOrEqualTo(0));
		Assert.That(unsubscribeShallow, Is.LessThan(subscribeDeep));
		Assert.That(subscribeShallowAgain, Is.GreaterThan(unsubscribeShallow));
		Assert.That(unsubscribeDeep, Is.LessThan(subscribeShallowAgain));

		upstream.Dispose();
		await upstream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task ConsecutiveFailuresUseOneTwoFourSecondBackoffWithoutASleep()
	{
		var clock = new ManualTimeProvider();
		var attempts = new ConcurrentQueue<DateTimeOffset>();
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ =>
			{
				attempts.Enqueue(clock.GetUtcNow());
				return Task.FromException<ITInvestMarketDataStream>(new TInvestRequestException("Invented failure."));
			}
		};
		using var upstream = new UpstreamStream(gateway, clock, TimeSpan.FromSeconds(180), _ =>
		{
		}, (_, _) =>
		{
		}, (_, _) =>
		{
		}, () => 0.5);
		upstream.SetDesired(new[]
		{
			"invented-uid"
		});
		for (var attempt = 1; attempt <= 3; attempt++)
		{
			await TInvestMarketDataHubTests.Until(() => attempts.Count == attempt && clock.ActiveTimers == 1);
			var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
			clock.Advance(delay - TimeSpan.FromMilliseconds(1));
			Assert.That(attempts.Count, Is.EqualTo(attempt));
			clock.Advance(TimeSpan.FromMilliseconds(1));
		}

		await TInvestMarketDataHubTests.Until(() => attempts.Count == 4);
		var times = attempts.ToArray();
		Assert.That(times.Zip(times.Skip(1), (a, b) => b - a), Is.EqualTo(new[]
		{
			TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(2),
			TimeSpan.FromSeconds(4)
		}));
		upstream.Dispose();
		await upstream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[TestCase(1)]
	[TestCase(2)]
	public async Task StreamCapacityCountsTwoMarketSubscriptionsPerInstrument(int maximumStreams)
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway
		{
			Snapshot = (_, ct) => new TaskCompletionSource<TInvestMarketSnapshot>().Task.WaitAsync(ct)
		};
		using var hub = new TInvestMarketDataHub(gateway, new TInvestOptions
		{
			MaxStreams = maximumStreams
		}, clock, persistRecording: false);
		var tasks = Enumerable.Range(0, maximumStreams * 150 + 1).Select(i => hub.SubscribeAsync(new TInvestSymbol(ScriptedTInvestGateway.Invent("INVENTED" + i)), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default)).ToArray();
		await hub.BarrierAsync();
		var error = Assert.ThrowsAsync<TInvestRequestException>(async () => await tasks[^1]);
		Assert.That(error!.Message, Does.Contain("Server subscription limit reached"));
		Assert.That(tasks.SkipLast(1).Any(t => t.IsCompleted), Is.False);
		hub.Dispose();
		foreach (var task in tasks.SkipLast(1))
		{
			try
			{
				using var subscription = await task;
			}
			catch (OperationCanceledException)
			{
			}
		}

		await hub.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[TestCase(TInvestInstrumentKind.StockOption)]
	[TestCase(TInvestInstrumentKind.FuturesOption)]
	[TestCase(TInvestInstrumentKind.IndexOption)]
	public void OptionsStayDefinitionsOnly(TInvestInstrumentKind kind)
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		using var hub = new TInvestMarketDataHub(gateway, new TInvestOptions(), clock, persistRecording: false);
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => hub.SubscribeAsync(new TInvestSymbol(ScriptedTInvestGateway.Invent(kind: kind)), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Unsupported));
	}

	static TInvestStreamRequest[] Requests(ScriptedMarketDataStream stream)
	{
		lock (stream.Requests)
		{
			return stream.Requests.ToArray();
		}
	}
}
