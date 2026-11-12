namespace SomeDataProvider.DataStorage.TInvest.Test;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;
using static SomeDataProvider.DataStorage.TInvest.Test.TickFixture;

/// <summary>Regression tests for the findings of the first independent review of the tick history.</summary>
[TestFixture]
public class TickReviewFixTests
{
	[Test]
	public async Task AnOpenEndedRangeIsServedUpToTheMomentTheAnswerIsAssembled()
	{
		using var fixture = new TickFixture();
		fixture.Recording.Intervals.Add((new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 30, 11, 30, 0, TimeSpan.Zero)));
		fixture.Archive(10, 29, Gzip(Csv(Row("2026-10-29T20:00:00Z", "SELL", "7", 1))));
		// The archive wait takes 5 s; a trade made during it is already in the last-hour answer and must not be dropped.
		fixture.Gateway.BeforeArchive = () =>
		{
			fixture.Clock.Advance(TimeSpan.FromSeconds(5));
			return Task.CompletedTask;
		};
		fixture.Gateway.LastTrades = (_, _) => new[] { Trade(Utc(10, 30, 12, 0, 3), 0, 5, 1) };
		var ticks = await ReadAll(await fixture.Ticks(Utc(10, 29, 12)));
		Assert.That(ticks.Select(t => t.LastPrice), Is.EqualTo(new[] { 7d, 5 }));
		Assert.That(fixture.Gateway.LastTradeRequests.Single().To, Is.EqualTo(Now.AddSeconds(5)), "The frontier is the assembly time.");
	}

	[Test]
	public async Task AClosedEndAtPlanningTimeIsNotExtended()
	{
		using var fixture = new TickFixture();
		fixture.Recording.Intervals.Add((new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 30, 11, 30, 0, TimeSpan.Zero)));
		fixture.Archive(10, 29, Gzip(Csv(Row("2026-10-29T20:00:00Z", "SELL", "7", 1))));
		fixture.Gateway.BeforeArchive = () =>
		{
			fixture.Clock.Advance(TimeSpan.FromSeconds(5));
			return Task.CompletedTask;
		};
		fixture.Gateway.LastTrades = (_, _) => new[] { Trade(Utc(10, 30, 12, 0, 3), 0, 5, 1) };
		var ticks = await ReadAll(await fixture.Ticks(Utc(10, 29, 12), Utc(10, 30, 12)));
		Assert.That(ticks.Select(t => t.LastPrice), Is.EqualTo(new[] { 7d }));
	}

	[Test]
	public void RecordedCoverageThatVanishesBeforeTheReadRejectsInsteadOfServingAnIncompleteDay()
	{
		using var fixture = new TickFixture();
		fixture.Recording.Intervals.Add((new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 30, 11, 30, 0, TimeSpan.Zero)));
		fixture.Recording.Trades.Add(Trade(Utc(10, 30, 10), 0, 1, 1));
		fixture.Recording.OnRead = () => fixture.Recording.Intervals.Clear(); // Cleanup pruned the day in between.
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 30, 9)));
		Assert.That(error!.Message, Does.Contain("changed while the answer was prepared"));
	}

	[Test]
	public void APinnedArchiveSurvivesEvictionUntilItsLeaseIsReleased()
	{
		var folder = Path.Combine(Path.GetTempPath(), "sdp-pin-" + Guid.NewGuid().ToString("N"));
		try
		{
			var cache = new TradeArchiveCache(folder, 10, new ManualTimeProvider());
			var older = cache.PathFor("invented-uid", new DateOnly(2026, 10, 28));
			var newer = cache.PathFor("invented-uid", new DateOnly(2026, 10, 29));
			Directory.CreateDirectory(Path.GetDirectoryName(older)!);
			File.WriteAllBytes(older, new byte[8]);
			File.WriteAllBytes(newer, new byte[8]);
			File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
			var lease = cache.TryPin(older)!;
			Assert.That(lease, Is.Not.Null);
			cache.Evict(newer);
			Assert.That(File.Exists(older), Is.True, "In use by a request.");
			var second = cache.TryPin(older)!;
			lease.Dispose();
			cache.Evict(newer);
			Assert.That(File.Exists(older), Is.True, "Two leases: one is still held.");
			second.Dispose();
			second.Dispose();
			cache.Evict(newer);
			Assert.That(File.Exists(older), Is.False, "Least recently used goes once nothing holds it.");
			Assert.That(File.Exists(newer), Is.True);
			Assert.That(cache.TryPin(older), Is.Null, "A missing file cannot be pinned.");
		}
		finally
		{
			try { Directory.Delete(folder, true); }
			catch (IOException) { }
		}
	}

	[Test]
	public void AnArchiveLargerThanTheWholeCacheIsRejectedAndNotKept()
	{
		using var fixture = new TickFixture(cacheBytes: 100L * 1024 * 1024);
		fixture.Gateway.ArchiveFiles[(InstrumentId, new DateOnly(2026, 10, 28))] = new byte[100 * 1024 * 1024 + 1];
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 28), Utc(10, 29)));
		Assert.That(error!.Message, Does.Contain("larger than ArchiveCacheMaxBytes"));
		var files = Directory.Exists(fixture.Folder) ? Directory.GetFiles(fixture.Folder, "*", SearchOption.AllDirectories) : Array.Empty<string>();
		Assert.That(files, Is.Empty, "Nothing above the cap stays on disk.");
	}

	[Test]
	public async Task ACountThatOutlastsThePreHeaderBudgetAnswersRetryInAndFinishesInTheBackground()
	{
		using var fixture = new TickFixture();
		fixture.Archive(10, 28, Gzip(Csv(Row("2026-10-28T07:00:00Z", "BUY", "1", 1))));
		using var started = new ManualResetEventSlim();
		using var gate = new ManualResetEventSlim();
		fixture.Store.BeforeArchiveCount = _ =>
		{
			started.Set();
			gate.Wait(TimeSpan.FromSeconds(30));
		};
		var request = fixture.Ticks(Utc(10, 28), Utc(10, 29));
		Assert.That(started.Wait(TimeSpan.FromSeconds(10)), Is.True);
		fixture.Clock.Advance(TimeSpan.FromSeconds(21));
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => request);
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		Assert.That(error.Message, Does.Contain("retry in"));
		gate.Set();
		for (var i = 0; i < 200; i++)
		{
			try
			{
				var reader = await fixture.Ticks(Utc(10, 28), Utc(10, 29));
				Assert.That(reader.Total, Is.EqualTo(1));
				return;
			}
			catch (TInvestRequestException ex) when (ex.Kind == TInvestFailureKind.Limit)
			{
				await Task.Delay(20);
			}
		}

		Assert.Fail("The background count never finished.");
	}

	[Test]
	public async Task ADayLoadThatOutlastsTheReadBudgetClosesTheAnswerWithoutAFinalRecord()
	{
		using var fixture = new TickFixture();
		fixture.Archive(10, 28, Gzip(Csv(Row("2026-10-28T07:00:00Z", "BUY", "1", 1))));
		using var reader = await fixture.Ticks(Utc(10, 28), Utc(10, 29));
		using var started = new ManualResetEventSlim();
		using var gate = new ManualResetEventSlim();
		fixture.Store.BeforeArchiveLoad = _ =>
		{
			started.Set();
			gate.Wait(TimeSpan.FromSeconds(30));
		};
		var read = reader.ReadSymbolHistoryAsync();
		Assert.That(started.Wait(TimeSpan.FromSeconds(10)), Is.True);
		fixture.Clock.Advance(TimeSpan.FromSeconds(21));
		try
		{
			// The load is still blocked inside synchronous work: the await itself must give up.
			var error = Assert.ThrowsAsync<TInvestRequestException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(10)));
			Assert.That(error!.Message, Does.Contain("timed out"));
		}
		finally
		{
			gate.Set();
		}
	}

	[Test]
	public void AnOpenEndedRequestThatCrossesMidnightWhilePreparingIsToldToRetry()
	{
		using var fixture = new TickFixture();
		fixture.Clock.Advance(new TimeSpan(11, 59, 58)); // 23:59:58 UTC on the planning day.
		fixture.Archive(10, 29, Gzip(Csv(Row("2026-10-29T20:00:00Z", "SELL", "7", 1))));
		fixture.Gateway.BeforeArchive = () =>
		{
			fixture.Clock.Advance(TimeSpan.FromSeconds(5));
			return Task.CompletedTask;
		};
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 29, 12)));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		Assert.That(error.Message, Does.Contain("retry in"));
	}

	[Test]
	public void AnArchiveAboveALoweredCapIsNeverServedFromTheCache()
	{
		var folder = Path.Combine(Path.GetTempPath(), "sdp-cap-" + Guid.NewGuid().ToString("N"));
		try
		{
			var cache = new TradeArchiveCache(folder, 10, new ManualTimeProvider());
			var path = cache.PathFor("invented-uid", new DateOnly(2026, 10, 28));
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, new byte[11]);
			Assert.That(cache.TryGet("invented-uid", new DateOnly(2026, 10, 28), out _), Is.False);
			Assert.That(File.Exists(path), Is.False);
		}
		finally
		{
			try { Directory.Delete(folder, true); }
			catch (IOException) { }
		}
	}

	[Test]
	public void EvictionDeferredByAPinRunsWhenTheLastLeaseIsReleased()
	{
		var folder = Path.Combine(Path.GetTempPath(), "sdp-defer-" + Guid.NewGuid().ToString("N"));
		try
		{
			var cache = new TradeArchiveCache(folder, 10, new ManualTimeProvider());
			var older = cache.PathFor("invented-uid", new DateOnly(2026, 10, 28));
			var newer = cache.PathFor("invented-uid", new DateOnly(2026, 10, 29));
			Directory.CreateDirectory(Path.GetDirectoryName(older)!);
			File.WriteAllBytes(older, new byte[8]);
			File.WriteAllBytes(newer, new byte[8]);
			File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
			var lease = cache.TryPin(older.Replace('\\', '/'))!; // Another spelling of the same path.
			cache.Evict(newer);
			Assert.That(File.Exists(older), Is.True);
			lease.Dispose();
			Assert.That(File.Exists(older), Is.False, "Over the cap with nothing holding the file, so it goes at once.");
			Assert.That(File.Exists(newer), Is.True, "The newest file is the least recently used last.");
		}
		finally
		{
			try { Directory.Delete(folder, true); }
			catch (IOException) { }
		}
	}

	[Test]
	public async Task NonExchangeRowsInTheLastHourAnswerAreDropped()
	{
		using var fixture = new TickFixture();
		fixture.Recording.Intervals.Add((new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 30, 11, 30, 0, TimeSpan.Zero)));
		fixture.Gateway.LastTrades = (_, _) => new[] { Trade(Utc(10, 30, 11, 45), 0, 1, 1, exchange: false), Trade(Utc(10, 30, 11, 50), 0, 2, 1) };
		var ticks = await ReadAll(await fixture.Ticks(Utc(10, 30, 9)));
		Assert.That(ticks.Select(t => t.LastPrice), Is.EqualTo(new[] { 2d }), "A non-exchange row in the answer is dropped.");
	}

	[Test]
	public async Task CancellationBetweenBatchesOfOneLoadedDayStopsTheReader()
	{
		using var fixture = new TickFixture();
		var rows = Enumerable.Range(0, TInvestTickReader.BatchSize * 2 + 5)
			.Select(i => Row("2026-10-29T07:" + (i / 60 % 60).ToString("00") + ":" + (i % 60).ToString("00") + "Z", "BUY", "1", 1))
			.ToArray();
		fixture.Archive(10, 29, Gzip(Csv(rows)));
		using var reader = await fixture.Ticks(Utc(10, 29, 0), Utc(10, 30, 0));
		using var cts = new CancellationTokenSource();
		var first = await reader.ReadSymbolHistoryAsync(cts.Token);
		Assert.That(first.HasMore, Is.True);
		cts.Cancel();
		Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadSymbolHistoryAsync(cts.Token));
	}

	[TestCase(28)]
	[TestCase(29)]
	public async Task MissingDaysNeverLeaveAnInferredEmptyCacheAcrossRestart(int day)
	{
		using var fixture = new TickFixture();
		Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, day), Utc(10, day + 1)));
		await fixture.Store.BackgroundFills;
		fixture.Store.Dispose();
		using var gateway = new ScriptedTInvestGateway { Limiter = new(fixture.Clock) };
		using var restarted = new TInvestStore(gateway, fixture.Store.Options, fixture.Clock);
		Assert.ThrowsAsync<TInvestRequestException>(() => restarted.CreateTickReaderAsync(fixture.Symbol, Utc(10, day), Utc(10, day + 1), fixture.Deadline()));
		Assert.That(gateway.ArchiveRequests, Has.Count.EqualTo(1), "A missing archive is not a persistent empty result.");
	}
}
