namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Market;

[TestFixture]
public class TInvestLiveHardeningTests
{
	[Test]
	public async Task IncompleteGetMySubscriptionsFencesTheStreamGenerationBeforeAnotherQuery()
	{
		var clock = new ManualTimeProvider();
		const string uid = "synthetic-overlap";
		var old = new ScriptedMarketDataStream();
		var next = new ScriptedMarketDataStream();
		var opened = 0;
		var oldQueries = 0;
		var nextQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var ready = new ConcurrentQueue<bool>();
		old.Sending = (request, self) =>
		{
			self.AutoReply = request.Kind != TInvestSubscriptionKind.All;
			if (request.Kind == TInvestSubscriptionKind.All)
			{
				System.Threading.Interlocked.Increment(ref oldQueries);
				Partial(self);
			}
		};
		next.Sending = (request, self) =>
		{
			self.AutoReply = request.Kind != TInvestSubscriptionKind.All;
			if (request.Kind == TInvestSubscriptionKind.All)
			{
				Partial(self);
				nextQuery.TrySetResult();
			}
		};
		using var gateway = new ScriptedTInvestGateway
		{
			OpenStream = _ => Task.FromResult<ITInvestMarketDataStream>(System.Threading.Interlocked.Increment(ref opened) == 1 ? old : next)
		};
		using var upstream = new UpstreamStream(gateway, clock, TimeSpan.FromSeconds(180), item =>
		{
			if (item is TInvestPing) marker.TrySetResult();
		}, (_, live) => ready.Enqueue(live), (_, _) => { }, () => 0.5);
		upstream.SetDesired(new[] { uid });
		await TInvestMarketDataHubTests.Pump(clock, () => old.Disposed || System.Threading.Volatile.Read(ref oldQueries) > 1);
		Assert.That(old.Disposed, Is.True, "A timed-out query has no wire request ID: retire its generation instead of overlapping it.");
		Assert.That(oldQueries, Is.EqualTo(1));
		await TInvestMarketDataHubTests.Pump(clock, () => nextQuery.Task.IsCompleted);
		old.Emit(Book());
		next.Emit(new TInvestPing());
		await marker.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.That(ready.Contains(true), Is.False, "Late replies from the old generation cannot prove the new query.");
		next.Emit(Book());
		await TInvestMarketDataHubTests.Until(() => ready.Contains(true));
		upstream.Dispose();
		await upstream.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		static TInvestSubscriptionReply Book() => new(TInvestSubscriptionKind.Book,
			new[] { new TInvestSubscriptionElement(uid, TInvestSubscriptionStatus.Success, 1) });
		static void Partial(ScriptedMarketDataStream stream)
		{
			stream.Emit(new TInvestSubscriptionReply(TInvestSubscriptionKind.Trades, new[] { new TInvestSubscriptionElement(uid, TInvestSubscriptionStatus.Success) }));
			stream.Emit(new TInvestSubscriptionReply(TInvestSubscriptionKind.Info, new[] { new TInvestSubscriptionElement(uid, TInvestSubscriptionStatus.Success) }));
		}
	}

	[TestCase(TInvestSubscriptionKind.Trades)]
	[TestCase(TInvestSubscriptionKind.Info)]
	[TestCase(TInvestSubscriptionKind.Book)]
	public async Task FailedUnsubscribeIsNotASubscribeReject(TInvestSubscriptionKind failedKind)
	{
		var clock = new ManualTimeProvider();
		var stream = new ScriptedMarketDataStream
		{
			RequestStatus = (request, _) => !request.Subscribe && request.Kind == failedKind
				? TInvestSubscriptionStatus.InternalError : TInvestSubscriptionStatus.Success
		};
		using var gateway = new ScriptedTInvestGateway { OpenStream = _ => Task.FromResult<ITInvestMarketDataStream>(stream) };
		var ready = new ConcurrentQueue<string>();
		var rejected = new ConcurrentQueue<string>();
		var diagnostics = new ConcurrentQueue<string>();
		using var upstream = new UpstreamStream(gateway, clock, TimeSpan.FromSeconds(180), _ => { },
			(uid, live) => { if (live) ready.Enqueue(uid); }, (uid, _) => rejected.Enqueue(uid),
			diagnostic: diagnostics.Enqueue, rejectedWithKind: (uid, _, _) => rejected.Enqueue(uid));
		upstream.SetDesired(new[] { "synthetic-anchor", "synthetic-removed" });
		await TInvestMarketDataHubTests.Pump(clock, () => ready.Contains("synthetic-removed"));
		var anchorReady = ready.Count(uid => uid == "synthetic-anchor");
		upstream.SetDesired(new[] { "synthetic-anchor" });
		await TInvestMarketDataHubTests.Pump(clock, () => ready.Count(uid => uid == "synthetic-anchor") > anchorReady);
		Assert.That(rejected, Is.Empty, "Removing a consumer must not report a subscribe rejection.");
		Assert.That(diagnostics.Any(text => text.Contains("unsubscribe", StringComparison.OrdinalIgnoreCase)), Is.True);
		Assert.That(stream.Disposed, Is.False);
		upstream.Dispose();
		await upstream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}
}
