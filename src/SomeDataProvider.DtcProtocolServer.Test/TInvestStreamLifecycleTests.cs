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
}
