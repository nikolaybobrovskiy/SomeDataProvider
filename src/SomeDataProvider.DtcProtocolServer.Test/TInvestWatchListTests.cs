namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Market;

[TestFixture]
public class TInvestWatchListTests
{
	[Test]
	public async Task InitialResolutionFailureRetriesAtThirtySecondsAndRecordsWithoutUnarySnapshots()
	{
		var clock = new ManualTimeProvider();
		var snapshots = 0;
		var lookups = 0;
		using var gateway = new ScriptedTInvestGateway
		{
			Resolve = (_, _, _) => Interlocked.Increment(ref lookups) == 1 ? Task.FromException<TInvestInstrument?>(new TInvestRequestException("Invented temporary failure.")) : Task.FromResult<TInvestInstrument?>(ScriptedTInvestGateway.Invent()),
			Snapshot = (_, _) =>
			{
				Interlocked.Increment(ref snapshots);
				return Task.FromException<TInvestMarketSnapshot>(new TInvestRequestException("Watch lists must not request snapshots."));
			}
		};
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		var options = new TInvestOptions
		{
			RecordSymbols = new[]
			{
				"SYNTH-TESTCC"
			}
		};
		using var store = new TInvestStore(gateway, options, clock);
		using var recorder = new TInvestTickRecorder(null, clock);
		using var hub = new TInvestMarketDataHub(gateway, options, clock, recorder);
		var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		hub.WatchListRetrying += _ => retry.TrySetResult();
		var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		hub.Diagnostic += text =>
		{
			if (text == "Symbol feed available.")
			{
				available.TrySetResult();
			}
		};
		var startup = hub.StartWatchListAsync(store);
		await retry.Task.WaitAsync(TimeSpan.FromSeconds(5));
		// Store refresh, recorder flush and retry delay; no subscription deadline survives the failed attempt.
		await TInvestMarketDataHubTests.Until(() => clock.ActiveTimers == 3);
		clock.Advance(TimeSpan.FromSeconds(29));
		Assert.That(lookups, Is.EqualTo(1));
		clock.Advance(TimeSpan.FromSeconds(1));
		await TInvestMarketDataHubTests.Pump(clock, () => available.Task.IsCompleted);
		clock.Advance(TimeSpan.FromSeconds(2));
		gateway.Streams[0].Emit(new TInvestPing());
		await TInvestMarketDataHubTests.Pump(clock, () => recorder.Coverage(gateway.Instruments[0].Short.Uid, new DateOnly(1970, 1, 1)).Count != 0);
		Assert.That(lookups, Is.EqualTo(2));
		Assert.That(snapshots, Is.Zero);
		hub.Dispose();
		await startup.WaitAsync(TimeSpan.FromSeconds(5));
	}
}
