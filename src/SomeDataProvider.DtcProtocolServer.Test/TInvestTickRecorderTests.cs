namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Market;

[TestFixture]
public class TInvestTickRecorderTests
{
	[Test]
	public void CoverageSplitsAtUtcMidnightPreservesHolesAndSurvivesRestart()
	{
		var clock = new ManualTimeProvider();
		clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(1));
		var root = Scratch();
		try
		{
			using (var recorder = new TInvestTickRecorder(root, clock))
			{
				recorder.SetLive("invented-uid", true);
				clock.Advance(TimeSpan.FromSeconds(3));
				recorder.Confirm("invented-uid", clock.GetUtcNow());
				recorder.SetLive("invented-uid", false);
				clock.Advance(TimeSpan.FromSeconds(10));
				recorder.SetLive("invented-uid", true);
				clock.Advance(TimeSpan.FromSeconds(2));
				recorder.Confirm("invented-uid", clock.GetUtcNow());
				recorder.SetLive("invented-uid", false);
				Assert.That(recorder.Coverage("invented-uid", new DateOnly(1970, 1, 1)).Single(), Is.EqualTo(new CoverageInterval(DateTimeOffset.UnixEpoch.AddDays(1).AddSeconds(-1), DateTimeOffset.UnixEpoch.AddDays(1))));
				var next = recorder.Coverage("invented-uid", new DateOnly(1970, 1, 2));
				Assert.That(next.Count, Is.EqualTo(2));
				Assert.That(next[1].Start - next[0].End, Is.EqualTo(TimeSpan.FromSeconds(11)));
			}

			clock.Advance(TimeSpan.FromSeconds(10));
			using var restarted = new TInvestTickRecorder(root, clock);
			Assert.That(restarted.Coverage("invented-uid", new DateOnly(1970, 1, 2)).Count, Is.EqualTo(2));
			Assert.That(restarted.Coverage("invented-uid", new DateOnly(1970, 1, 2))[^1].End, Is.LessThan(clock.GetUtcNow()), "Restart never extends the previous live interval.");
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Test]
	public void ExchangeRecordsKeepNanosecondsAndFlushOncePerSecond()
	{
		var root = Scratch();
		var clock = new ManualTimeProvider();
		try
		{
			using (var recorder = new TInvestTickRecorder(root, clock))
			{
				recorder.SetLive("invented-uid", true);
				recorder.Record(new("invented-uid", new(100, 123456789), 12.5, 3, TInvestDirection.Buy, true));
				recorder.Record(new("invented-uid", new(100, 123456790), 13, 2, TInvestDirection.Sell, false));
				clock.Advance(TimeSpan.FromSeconds(1));
				Assert.That(recorder.RecordedTrades, Is.EqualTo(1));
				using var read = new BinaryReader(File.Open(Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories).Single(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
				Assert.That(read.ReadInt64(), Is.EqualTo(100));
				Assert.That(read.ReadInt32(), Is.EqualTo(123456789));
				Assert.That(read.ReadDouble(), Is.EqualTo(12.5));
				Assert.That(read.ReadInt64(), Is.EqualTo(3));
				Assert.That(read.ReadByte(), Is.EqualTo((byte)TInvestDirection.Buy));
				Assert.That(read.BaseStream.Position, Is.EqualTo(read.BaseStream.Length));
			}

			var file = Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories).Single();
			using (var torn = File.Open(file, FileMode.Append))
				torn.WriteByte(99);
			using (var restarted = new TInvestTickRecorder(root, clock))
				restarted.Record(new("invented-uid", new(101, 0), 12.6, 4, TInvestDirection.Sell, true));
			Assert.That(new FileInfo(file).Length, Is.EqualTo(2 * (sizeof(long) + sizeof(int) + sizeof(double) + sizeof(long) + sizeof(byte))));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Test]
	public void DiskFailureStopsCoverageInsteadOfClaimingFutureTicks()
	{
		var root = Scratch();
		var clock = new ManualTimeProvider();
		File.WriteAllText(root, "invented blocker");
		try
		{
			using var recorder = new TInvestTickRecorder(root, clock);
			recorder.SetLive("invented-uid", true);
			recorder.Record(new("invented-uid", new(0, 0), 12, 1, TInvestDirection.Buy, true));
			clock.Advance(TimeSpan.FromSeconds(5));
			Assert.That(recorder.Failed, Is.True);
			Assert.That(recorder.Coverage("invented-uid", new DateOnly(1970, 1, 1)), Is.Empty);
		}
		finally
		{
			File.Delete(root);
		}
	}

	[Test]
	public async Task WatchListRecordsWithNoDtcClients()
	{
		var clock = new ManualTimeProvider();
		var options = new TInvestOptions
		{
			RecordSymbols = new[]
			{
				"SYNTH-TESTCC"
			}
		};
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var store = new TInvestStore(gateway, options, clock);
		using var recorder = new TInvestTickRecorder(null, clock);
		using var hub = new TInvestMarketDataHub(gateway, options, clock, recorder);
		var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		hub.Diagnostic += text =>
		{
			if (text == "Symbol feed available.")
			{
				available.TrySetResult();
			}
		};
		var start = hub.StartWatchListAsync(store);
		await TInvestMarketDataHubTests.Pump(clock, () => available.Task.IsCompleted);
		clock.Advance(TimeSpan.FromSeconds(2));
		gateway.Streams.Single().Emit(new TInvestPing());
		await TInvestMarketDataHubTests.Pump(clock, () => recorder.Coverage(gateway.Instruments[0].Short.Uid, new DateOnly(1970, 1, 1)).Count != 0);
		gateway.Streams.Single().Emit(new TInvestTrade(gateway.Instruments[0].Short.Uid, new(1, 1), 12, 1, TInvestDirection.Buy, true));
		await TInvestMarketDataHubTests.Until(() => recorder.RecordedTrades == 1);
		clock.Advance(TimeSpan.FromSeconds(2));
		Assert.That(recorder.Coverage(gateway.Instruments[0].Short.Uid, new DateOnly(1970, 1, 1)), Is.Not.Empty);
		hub.Dispose();
		await start.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[TestCase(180)]
	[TestCase(900)]
	public void SilentStallNeverExtendsCoverageToWatchdogTime(int silence)
	{
		var root = Scratch();
		var clock = new ManualTimeProvider();
		try
		{
			using (var recorder = new TInvestTickRecorder(root, clock))
			{
				recorder.SetLive("invented-uid", true);
				clock.Advance(TimeSpan.FromSeconds(10));
				recorder.Confirm("invented-uid", clock.GetUtcNow());
				recorder.Flush();
				clock.Advance(TimeSpan.FromSeconds(silence));
				recorder.SetLive("invented-uid", false);
				Assert.That(recorder.Coverage("invented-uid", new DateOnly(1970, 1, 1)).Single().End, Is.EqualTo(DateTimeOffset.UnixEpoch.AddSeconds(9)));
			}

			using var restarted = new TInvestTickRecorder(root, clock);
			Assert.That(restarted.Coverage("invented-uid", new DateOnly(1970, 1, 1)).Single().End, Is.EqualTo(DateTimeOffset.UnixEpoch.AddSeconds(9)));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Test]
	public async Task SlowDiskDoesNotHoldTheHubFacingLockAndPastDaysClose()
	{
		var root = Scratch();
		var clock = new ManualTimeProvider();
		using var release = new System.Threading.ManualResetEventSlim();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var recorder = new TInvestTickRecorder(root, clock, () =>
		{
			entered.TrySetResult();
			release.Wait();
		});
		recorder.Record(new("invented-uid", new(1, 0), 12, 1, TInvestDirection.Buy, true));
		var flush = Task.Run(recorder.Flush);
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			await Task.Run(() => recorder.Record(new("invented-uid", new(2, 0), 12.5, 1, TInvestDirection.Buy, true))).WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			release.Set();
			await flush.WaitAsync(TimeSpan.FromSeconds(5));
		}

		try
		{
			clock.Advance(TimeSpan.FromDays(1));
			using var exclusive = File.Open(Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories).Single(), FileMode.Open, FileAccess.Read, FileShare.None);
			Assert.That(exclusive.Length, Is.EqualTo(58));
		}
		finally
		{
			recorder.Dispose();
			Directory.Delete(root, true);
		}
	}

	static string Scratch() => Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "tinvest-recorder-" + Guid.NewGuid().ToString("N"));
}
