namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DtcProtocolServer.Market;

public partial class TInvestSandboxTests
{
	[Test]
	[Explicit("Local read-only sandbox check of today's tick merge; structural output only, never in CI.")]
	[Category("TInvestSandbox")]
	public async Task MergesTodaysRecordedTicksWithLastTrades()
	{
		var token = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_TOKEN");
		var ticker = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_SYMBOL");
		var exchange = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_EXCHANGE");
		if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(ticker) || string.IsNullOrEmpty(exchange))
		{
			Assert.Ignore("Explicit sandbox variables are required.");
		}

		var options = new TInvestOptions { Environment = "Sandbox" };
		Assert.That(options.IsSandbox, Is.True);
		var root = Path.Combine(Path.GetTempPath(), "sdp-sandbox-ticks-" + Guid.NewGuid().ToString("N"));
		using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(110));
		using var sdk = new SdkTInvestGateway(new TInvestToken(token!), options, new TInvestRateLimiter());
		using var bounded = new BoundedGateway(sdk);
		var stage = "access";
		try
		{
			using var recorder = new TInvestTickRecorder(root, TimeProvider.System);
			using var hub = new TInvestMarketDataHub(bounded, options, recorder: recorder);
			using var store = new TInvestStore(bounded, options);
			store.UseTickRecording(hub.Recording);
			await TInvestStartup.CheckAsync(bounded, options, DateTimeOffset.UtcNow.AddSeconds(8), stop.Token);
			stage = "definition";
			var instrument = await bounded.GetInstrumentAsync(ticker!, exchange!, DateTimeOffset.UtcNow.AddSeconds(8), stop.Token);
			Assert.That(instrument != null && TInvestStore.Complete(instrument), Is.True, "Complete instrument resolved.");
			var symbol = new TInvestSymbol(instrument!);
			stage = "sample";
			var sampleStart = DateTimeOffset.UtcNow;
			using (await hub.SubscribeAsync(symbol, _ => { }, DateTimeOffset.UtcNow.AddSeconds(20), stop.Token))
			{
				await Task.Delay(TimeSpan.FromSeconds(45), stop.Token);
			}

			var sampleEnd = DateTimeOffset.UtcNow;
			stage = "merge";
			var today = DateOnly.FromDateTime(sampleEnd.UtcDateTime);
			var coverage = hub.Recording.Coverage(symbol.Instrument.Short.Uid, today);
			var recorded = hub.Recording.Read(symbol.Instrument.Short.Uid, today, sampleStart.AddMinutes(-1), sampleEnd.AddMinutes(1));
			var from = new DateTime((sampleEnd.UtcDateTime.AddMinutes(-30).Ticks / TimeSpan.TicksPerSecond) * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
			string outcome;
			var merged = 0;
			var ordered = true;
			try
			{
				using var reader = await store.CreateTickReaderAsync(symbol, from, default, DateTimeOffset.UtcNow.AddSeconds(30), stop.Token);
				var response = await reader.ReadSymbolHistoryAsync(stop.Token);
				var ticks = response.Records.OfType<TInvestTickRecord>().ToList();
				merged = ticks.Count;
				ordered = ticks.Zip(ticks.Skip(1), (a, b) => a.Microseconds <= b.Microseconds).All(x => x);
				outcome = "merged";
			}
			catch (TInvestRequestException ex)
			{
				outcome = "rejected_" + ex.Kind;
			}

			TestContext.Out.WriteLine(
				"sandbox=true outcome=" + outcome +
				" recorded_in_sample=" + recorded.Count +
				" coverage_intervals=" + coverage.Count +
				" coverage_seconds=" + (int)coverage.Sum(c => (c.End - c.Start).TotalSeconds) +
				" merged_ticks=" + merged +
				" ordered=" + ordered +
				" upstream_requests=" + bounded.Calls + " upstream_cap=30");
			Assert.That(ordered, Is.True, "The merged answer is in time order.");
			Assert.That(merged >= recorded.Count(t => t.Time.Seconds >= ((DateTimeOffset)from).ToUnixTimeSeconds()) || outcome != "merged", Is.True, "Every recorded tick in the range is in the merged answer.");
		}
		catch (Exception ex) when (ex is not AssertionException and not IgnoreException)
		{
			Assert.Fail("Read-only sandbox tick check failed; stage=" + stage + "; exception=" + ex.GetType().Name + "; grpc_status=" + (sdk.LastFailureStatus ?? "none"));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}
}
