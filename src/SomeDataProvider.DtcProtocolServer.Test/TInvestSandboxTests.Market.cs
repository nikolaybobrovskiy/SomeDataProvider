namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DtcProtocolServer.Market;

public partial class TInvestSandboxTests
{
	[Test]
	[Explicit("Local read-only sandbox stream through the hub; structural output only, never in CI.")]
	[Category("TInvestSandbox")]
	public async Task SamplesLiveStreamThroughHub()
	{
		var token = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_TOKEN");
		var ticker = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_SYMBOL");
		var exchange = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_EXCHANGE");
		if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(ticker) || string.IsNullOrEmpty(exchange))
		{
			Assert.Ignore("Explicit sandbox variables are required.");
		}

		var options = new TInvestOptions
		{
			Environment = "Sandbox"
		};
		Assert.That(options.Environment, Is.EqualTo("Sandbox"));
		using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(110));
		using var sdk = new SdkTInvestGateway(new TInvestToken(token!), options, new TInvestRateLimiter());
		using var bounded = new BoundedGateway(sdk);
		using var recorder = new TInvestTickRecorder(null, TimeProvider.System);
		using var hub = new TInvestMarketDataHub(bounded, options, recorder: recorder);
		var counts = new Dictionary<string, int>();
		var statuses = new Dictionary<string, int>();
		var pings = new List<long>();
		var clock = System.Diagnostics.Stopwatch.StartNew();
		var gate = new object();
		long? previousMessage = null;
		long longestMessageGap = 0;
		var snapshot = false;
		var snapshotStatus = TInvestTradingStatus.Unspecified;
		hub.Observed += item =>
		{
			lock (gate)
			{
				var now = clock.ElapsedMilliseconds;
				if (previousMessage.HasValue)
				{
					longestMessageGap = Math.Max(longestMessageGap, now - previousMessage.Value);
				}

				previousMessage = now;
				var kind = item.GetType().Name;
				counts[kind] = counts.GetValueOrDefault(kind) + 1;
				if (item is TInvestPing)
				{
					pings.Add(now);
				}

				if (item is TInvestSubscriptionReply reply)
				{
					foreach (var element in reply.Elements)
					{
						var key = reply.Kind + ":" + element.Status;
						statuses[key] = statuses.GetValueOrDefault(key) + 1;
					}
				}
			}
		};
		using var networkTimings = new SandboxNetworkTimings();
		var stage = "access";
		var startupClock = System.Diagnostics.Stopwatch.StartNew();
		long? accessMs = null;
		long? definitionMs = null;
		long? subscriptionMs = null;
		string? startupSummary = null;
		Exception? failure = null;
		try
		{
			await TInvestStartup.CheckAsync(bounded, options, DateTimeOffset.UtcNow.AddSeconds(8), stop.Token);
			accessMs = startupClock.ElapsedMilliseconds;
			stage = "definition";
			var instrument = await bounded.GetInstrumentAsync(ticker!, exchange!, DateTimeOffset.UtcNow.AddSeconds(8), stop.Token);
			definitionMs = startupClock.ElapsedMilliseconds - accessMs.Value;
			Assert.That(instrument != null && TInvestStore.Complete(instrument), Is.True, "Complete instrument resolved.");
			stage = "subscription";
			using var subscription = await hub.SubscribeAsync(new TInvestSymbol(instrument!), e =>
			{
				if (e is HubSnapshot initial)
				{
					lock (gate)
					{
						snapshot = true;
						snapshotStatus = initial.Snapshot.Status;
					}
				}
			}, DateTimeOffset.UtcNow.AddSeconds(20), stop.Token);
			subscriptionMs = startupClock.ElapsedMilliseconds - accessMs.Value - definitionMs.Value;
			startupSummary = networkTimings.Summary();
			networkTimings.Dispose();
			stage = "sample";
			await Task.Delay(TimeSpan.FromSeconds(70), stop.Token);
		}
		catch (Exception ex)
		{
			failure = ex;
		}
		finally
		{
			startupSummary ??= networkTimings.Summary();
			networkTimings.Dispose();
			hub.Dispose();
			await hub.Completion;
		}

		lock (gate)
		{
			var intervals = pings.Zip(pings.Skip(1), (a, b) => b - a).ToArray();
			var quiet = IsQuietSample(snapshotStatus, counts.GetValueOrDefault(nameof(TInvestTrade)), counts.GetValueOrDefault(nameof(TInvestQuote)));
			TestContext.Out.WriteLine("sandbox=true snapshot=" + snapshot + " snapshot_trading_status=" + snapshotStatus + " reserved_attempts=" + bounded.Calls + " upstream_cap=30 sample_ms=" + clock.ElapsedMilliseconds);
			TestContext.Out.WriteLine("message_counts=" + string.Join(",", counts.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value)));
			TestContext.Out.WriteLine("subscription_status_counts=" + string.Join(",", statuses.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value)));
			TestContext.Out.WriteLine("ping_count=" + pings.Count + " ping_intervals_ms=" + string.Join(",", intervals) + " longest_message_gap_ms=" + longestMessageGap + " silence_default_ms=" + options.StreamSilenceTimeout.TotalMilliseconds);
			TestContext.Out.WriteLine("startup_stage=" + stage + " " + startupSummary + " access_ms=" + (accessMs?.ToString() ?? "incomplete") + " definition_ms=" + (definitionMs?.ToString() ?? "incomplete") + " subscription_ms=" + (subscriptionMs?.ToString() ?? "incomplete"));
			TestContext.Out.WriteLine("q3=" + (quiet ? "quiet_sample" : "inconclusive_active_or_unknown_status"));
			if (failure != null)
			{
				Assert.Fail("Sandbox hub structural check failed; exception=" + failure.GetType().Name + "; grpc_status=" + (sdk.LastFailureStatus ?? "none") + "; api_code=" + (sdk.LastFailureCode ?? "none") + "; no live payload recorded.");
			}

			Assert.That(snapshot, Is.True, "Subscription snapshot observed.");
			Assert.That(clock.Elapsed < TimeSpan.FromSeconds(110), Is.True, "Stream sample is bounded.");
			if (!quiet)
			{
				Assert.Inconclusive("Q-3 requires a quiet stream with a known non-trading snapshot status; active streams need not send pings.");
			}

			Assert.That(intervals.Length >= 2 && intervals.All(i => i > 0), Is.True, "At least three pings on the quiet stream.");
			Assert.That(intervals.Max() * 3 <= options.StreamSilenceTimeout.TotalMilliseconds, Is.True, "Silence default is at least three measured ping intervals.");
		}
	}

	[TestCase(TInvestTradingStatus.SessionClose, 0, 0, true)]
	[TestCase(TInvestTradingStatus.NotAvailableForTrading, 0, 0, true)]
	[TestCase(TInvestTradingStatus.NormalTrading, 0, 0, false)]
	[TestCase(TInvestTradingStatus.SessionOpen, 0, 0, false)]
	[TestCase(TInvestTradingStatus.DealerNormalTrading, 0, 0, false)]
	[TestCase(TInvestTradingStatus.Unspecified, 0, 0, false)]
	[TestCase(TInvestTradingStatus.SessionClose, 1, 0, false)]
	[TestCase(TInvestTradingStatus.SessionClose, 0, 1, false)]
	public void ClassifiesQuietStreamSample(TInvestTradingStatus status, int trades, int quotes, bool quiet)
	{
		Assert.That(IsQuietSample(status, trades, quotes), Is.EqualTo(quiet));
	}

	static bool IsQuietSample(TInvestTradingStatus status, int trades, int quotes) => trades == 0 && quotes == 0 && Enum.IsDefined(status) && status is not (TInvestTradingStatus.Unspecified or TInvestTradingStatus.NormalTrading or TInvestTradingStatus.SessionOpen or TInvestTradingStatus.DealerNormalTrading);
}
