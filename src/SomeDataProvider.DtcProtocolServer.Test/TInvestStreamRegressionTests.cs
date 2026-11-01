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
public class TInvestStreamRegressionTests
{
	[TestCase(false)]
	[TestCase(true)]
	public async Task DesireChangesDuringWriteStillConsumeReplyAndTrackTheWrittenSet(bool readd)
	{
		var clock = new ManualTimeProvider();
		var stream = new ScriptedMarketDataStream();
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ => Task.FromResult<ITInvestMarketDataStream>(stream)
		};
		var ready = new ConcurrentQueue<string>();
		var errors = new ConcurrentQueue<TInvestSubscriptionStatus>();
		using var upstream = new UpstreamStream(gateway, clock, TimeSpan.FromSeconds(180), _ =>
		{
		}, (uid, live) =>
		{
			if (live)
			{
				ready.Enqueue(uid);
			}
		}, (_, status) => errors.Enqueue(status));
		var changed = 0;
		stream.Sending = (request, _) =>
		{
			if (request.Kind == TInvestSubscriptionKind.Trades && request.Subscribe != readd && request.Uids.Contains("invented-victim") && Interlocked.Exchange(ref changed, 1) == 0)
			{
				if (readd)
				{
					upstream.SetDesired(new[]
					{
						"invented-anchor",
						"invented-victim"
					});
				}
				else
				{
					upstream.SetDesired(new[]
					{
						"invented-anchor"
					});
				}
			}
		};
		if (readd)
		{
			upstream.SetDesired(new[]
			{
				"invented-anchor",
				"invented-victim"
			});
			await TInvestMarketDataHubTests.Pump(clock, () => ready.Contains("invented-victim"));
			upstream.SetDesired(new[]
			{
				"invented-anchor"
			});
		}
		else
		{
			upstream.SetDesired(new[]
			{
				"invented-anchor",
				"invented-victim"
			});
		}

		await TInvestMarketDataHubTests.Pump(clock, () => Requests(stream).Count(r => r.Kind == TInvestSubscriptionKind.Trades && r.Subscribe == readd && r.Uids.Contains("invented-victim")) == (readd ? 2 : 1));
		await TInvestMarketDataHubTests.Pump(clock, () => ready.Contains("invented-anchor"));
		Assert.That(errors, Is.Empty);
		Assert.That(stream.Disposed, Is.False);
		upstream.Dispose();
		await upstream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task LocalQuotaRejectsWaitingSubscriberWithoutBreakingExistingStream()
	{
		var clock = new ManualTimeProvider();
		var stream = new ScriptedMarketDataStream();
		var opens = 0;
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ =>
			{
				Interlocked.Increment(ref opens);
				return Task.FromResult<ITInvestMarketDataStream>(stream);
			}
		};
		using var hub = new TInvestMarketDataHub(gateway, new TInvestOptions(), clock, persistRecording: false);
		var events = new ConcurrentQueue<HubEvent>();
		var anchor = hub.SubscribeAsync(new(ScriptedTInvestGateway.Invent("ANCHOR")), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default);
		await TInvestMarketDataHubTests.Pump(clock, () => anchor.IsCompleted);
		using var keep = await anchor;
		var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		stream.Sending = (request, _) =>
		{
			if (request.Uids.Contains("invented-uid-LATER"))
			{
				attempted.TrySetResult();
				throw new TInvestRequestException("Invented quota delay.", TInvestFailureKind.Limit);
			}
		};
		var later = hub.SubscribeAsync(new(ScriptedTInvestGateway.Invent("LATER")), _ =>
		{
		}, clock.GetUtcNow().AddSeconds(20), default);
		await TInvestMarketDataHubTests.Pump(clock, () => attempted.Task.IsCompleted);
		clock.Advance(TimeSpan.FromSeconds(20));
		Assert.ThrowsAsync<TInvestRequestException>(async () => await later);
		await hub.BarrierAsync();
		stream.Emit(new TInvestTrade("invented-uid-ANCHOR", new(1, 0), 12, 1, TInvestDirection.Buy, true));
		await TInvestMarketDataHubTests.Until(() => events.OfType<HubUpdate>().Any());
		Assert.That(opens, Is.EqualTo(1));
		Assert.That(stream.Disposed, Is.False);
		Assert.That(events.OfType<HubFeedStatus>().Any(s => !s.Available), Is.False);
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task ElementFailureRetriesWithoutReconnectEvenWhenGetMyOmitsEmptyKinds(bool omitEmpty)
	{
		var clock = new ManualTimeProvider();
		var fail = true;
		var stream = new ScriptedMarketDataStream
		{
			OmitEmptyKinds = omitEmpty,
			Status = (kind, _) => kind == TInvestSubscriptionKind.Book && fail ? TInvestSubscriptionStatus.InternalError : TInvestSubscriptionStatus.Success
		};
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ => Task.FromResult<ITInvestMarketDataStream>(stream)
		};
		var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var upstream = new UpstreamStream(gateway, clock, TimeSpan.FromSeconds(180), _ =>
		{
		}, (_, live) =>
		{
			if (live)
			{
				ready.TrySetResult();
			}
		}, (_, _) =>
		{
		}, diagnostic: text =>
		{
			if (text == "Subscription retry scheduled.")
			{
				retry.TrySetResult();
			}
		});
		upstream.SetDesired(new[]
		{
			"invented-uid"
		});
		await TInvestMarketDataHubTests.Pump(clock, () => retry.Task.IsCompleted);
		await TInvestMarketDataHubTests.Until(() => clock.ActiveTimers == 2);
		fail = false;
		clock.Advance(TimeSpan.FromSeconds(30));
		await TInvestMarketDataHubTests.Pump(clock, () => ready.Task.IsCompleted);
		Assert.That(stream.Disposed, Is.False);
		Assert.That(stream.Queries, Is.GreaterThanOrEqualTo(2));
		upstream.Dispose();
		await upstream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task SnapshotOnlyDoesNotOpenAnUpstreamSubscription()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		using var hub = new TInvestMarketDataHub(gateway, new TInvestOptions(), clock, persistRecording: false);
		var events = new ConcurrentQueue<HubEvent>();
		using var snapshot = await hub.SubscribeAsync(new(ScriptedTInvestGateway.Invent()), events.Enqueue, clock.GetUtcNow().AddSeconds(20), default, snapshotOnly: true).WaitAsync(TimeSpan.FromSeconds(5));
		Assert.That(gateway.Streams, Is.Empty);
		Assert.That(events.OfType<HubSnapshot>().Count(), Is.EqualTo(1));
	}

	[Test]
	public async Task IntentionalIdleCloseDoesNotBackoffOrReportReplacementUnavailable()
	{
		var clock = new ManualTimeProvider();
		var opens = 0;
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ =>
			{
				Interlocked.Increment(ref opens);
				return Task.FromResult<ITInvestMarketDataStream>(new ScriptedMarketDataStream());
			}
		};
		var statuses = new ConcurrentQueue<(string Uid, bool Available)>();
		var diagnostics = new ConcurrentQueue<string>();
		using var upstream = new UpstreamStream(gateway, clock, TimeSpan.FromSeconds(180), _ =>
		{
		}, (id, live) => statuses.Enqueue((id, live)), (_, _) =>
		{
		}, () => 0.5, diagnostic: diagnostics.Enqueue);
		for (var i = 0; i < 8; i++)
		{
			var uid = "invented-switch-" + i;
			var start = clock.GetUtcNow();
			upstream.SetDesired(Array.Empty<string>());
			upstream.SetDesired(new[]
			{
				uid
			});
			await TInvestMarketDataHubTests.Pump(clock, () => statuses.Any(s => s.Uid == uid && s.Available));
			Assert.That(clock.GetUtcNow() - start, Is.LessThan(TimeSpan.FromSeconds(1)), "An intentional close must not accrue failure backoff.");
		}

		Assert.That(statuses.Any(s => !s.Available), Is.False);
		Assert.That(diagnostics, Does.Not.Contain("Stream disconnected; reconnect scheduled."));
		Assert.That(opens, Is.EqualTo(8));
		upstream.Dispose();
		await upstream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	static TInvestStreamRequest[] Requests(ScriptedMarketDataStream stream)
	{
		lock (stream.Requests)
			return stream.Requests.ToArray();
	}
}
