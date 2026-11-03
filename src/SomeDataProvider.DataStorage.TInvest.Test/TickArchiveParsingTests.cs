namespace SomeDataProvider.DataStorage.TInvest.Test;

using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;
using static SomeDataProvider.DataStorage.TInvest.Test.TickFixture;

[TestFixture]
public class TickArchiveParsingTests
{
	static readonly string[] Rows =
	{
		Row("2026-10-28T06:59:53.115448Z", "BUY", "310.5", 3),
		Row("2026-10-28T07:00:00Z", "SELL", "310.25", 2),
		Row("2026-10-28T07:00:01.5Z", "BUY", "310", 1, "DEALER"),
	};

	[TestCase("gzip")]
	[TestCase("zip")]
	public async Task GzipAndZipGiveTheSameRecordsWithoutDealerRows(string format)
	{
		using var fixture = new TickFixture();
		var csv = Csv(Rows);
		fixture.Archive(10, 28, format == "gzip" ? Gzip(csv) : Zip(csv));
		var reader = await fixture.Ticks(Utc(10, 28), Utc(10, 29));
		var ticks = await ReadAll(reader);
		Assert.That(reader.Total, Is.EqualTo(2));
		Assert.That(ticks.Select(t => t.Microseconds), Is.EqualTo(new[] { Micros(10, 28, 6, 59, 53, 115448), Micros(10, 28, 7, 0, 0) }));
		Assert.That(ticks.Select(t => t.Direction), Is.EqualTo(new[] { TInvestDirection.Buy, TInvestDirection.Sell }));
		Assert.That(ticks.Select(t => t.Volume), Is.EqualTo(new[] { 30d, 20d }), "Volume is quantity x lot (lot 10).");
		Assert.That(ticks.Select(t => t.LastPrice), Is.EqualTo(new[] { 310.5, 310.25 }));
	}

	[Test]
	public async Task DealerRowsAreKeptWhenConfigured()
	{
		using var fixture = new TickFixture(dealer: true);
		fixture.Archive(10, 28, Gzip(Csv(Rows)));
		var ticks = await ReadAll(await fixture.Ticks(Utc(10, 28), Utc(10, 29)));
		Assert.That(ticks, Has.Count.EqualTo(3));
	}

	[Test]
	public async Task RowsAreStablySortedAndEqualTimesKeepFileOrder()
	{
		using var fixture = new TickFixture();
		fixture.Archive(10, 28, Gzip(Csv(
			Row("2026-10-28T07:00:02Z", "BUY", "3", 1),
			Row("2026-10-28T07:00:01Z", "BUY", "1", 1),
			Row("2026-10-28T07:00:01Z", "SELL", "2", 1),
			Row("2026-10-28T07:00:01Z", "BUY", "1.5", 1))));
		var ticks = await ReadAll(await fixture.Ticks(Utc(10, 28), Utc(10, 29)));
		Assert.That(ticks.Select(t => t.LastPrice), Is.EqualTo(new[] { 1, 2, 1.5, 3 }));
	}

	[Test]
	public async Task BoundsAreHalfOpenAndCountedWithTheSameRule()
	{
		using var fixture = new TickFixture();
		fixture.Archive(10, 28, Gzip(Csv(
			Row("2026-10-28T07:00:00Z", "BUY", "1", 1),
			Row("2026-10-28T07:00:01Z", "BUY", "2", 1),
			Row("2026-10-28T07:00:02Z", "BUY", "3", 1))));
		var reader = await fixture.Ticks(Utc(10, 28, 7, 0, 1), Utc(10, 28, 7, 0, 2));
		Assert.That(reader.Total, Is.EqualTo(1));
		Assert.That((await ReadAll(reader)).Select(t => t.LastPrice), Is.EqualTo(new[] { 2d }));
	}

	[TestCase("not an archive at all")]
	[TestCase("BAD_HEADER\n2026-10-28T07:00:00Z,SYNTH_TESTCC,BUY,1,1,EXCHANGE,u\n")]
	[TestCase("2026-10-27T23:59:59Z")] // Row on another UTC date than the file's.
	[TestCase("2026-10-28T07:00:00Z,SYNTH_TESTCC,BUY,abc,1,EXCHANGE,u")]
	[TestCase("2026-10-28T07:00:00Z,SYNTH_TESTCC,BUY,1,1,EXCHANGE")]
	public void MalformedArchivesFailBeforeAnyRecordAndAreNotKept(string content)
	{
		using var fixture = new TickFixture();
		var csv = content.StartsWith("BAD_HEADER") || content.StartsWith("not an") ? content : Csv(content.StartsWith("2026-10-27") ? Row(content, "BUY", "1", 1) : content);
		fixture.Archive(10, 28, content.StartsWith("not an") ? Encoding_(content) : Gzip(csv));
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 28), Utc(10, 29)));
		Assert.That(error!.Message, Does.Contain("unexpected format"));
		Assert.That(fixture.Store.ArchiveCache.TryGet("invented-uid-SYNTH", new DateOnly(2026, 10, 28), out _), Is.False, "A bad file is removed so the next request downloads it again.");
	}

	[Test]
	public void ZipWithSeveralEntriesIsRejected()
	{
		using var fixture = new TickFixture();
		fixture.Archive(10, 28, Zip(Csv(Rows), 2));
		Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 28), Utc(10, 29)));
	}

	[Test]
	public void BothFormatsAreRecognizedByMagicBytesOnly()
	{
		Assert.That(TradeArchiveReader.IsKnownFormat(new byte[] { 0x1F, 0x8B, 8, 0 }), Is.True);
		Assert.That(TradeArchiveReader.IsKnownFormat(new byte[] { 0x50, 0x4B, 3, 4 }), Is.True);
		Assert.That(TradeArchiveReader.IsKnownFormat(new byte[] { 0x50, 0x4B, 5, 6 }), Is.False);
		Assert.That(TradeArchiveReader.IsKnownFormat(System.Text.Encoding.ASCII.GetBytes("TRADE_TS")), Is.False);
	}

	static byte[] Encoding_(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}

[TestFixture]
public class TickRangeAndCapTests
{
	[Test]
	public void StartTimeIsRequiredAndBoundsAreWholeSeconds()
	{
		using var fixture = new TickFixture();
		Assert.That(Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(default))!.Message, Does.Contain("start time"));
		Assert.That(Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 28).AddTicks(1), Utc(10, 29)))!.Message, Does.Contain("whole UTC seconds"));
		Assert.That(Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 28), Utc(10, 28)))!.Message, Does.Contain("end must be after start"));
		Assert.That(Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 31)))!.Message, Does.Contain("end must be after start"), "A future start is an empty range.");
		Assert.That(fixture.Gateway.ArchiveRequests, Is.Empty);
	}

	[TestCase(1, 28, 28, true)]
	[TestCase(1, 28, 29, false)]
	[TestCase(2, 27, 28, true)]
	[TestCase(2, 26, 28, false)]
	public async Task DayCapRejectsBeforeAnyRequestAndTheEndsOfTheRangeAreAllowed(int maxDays, int firstDay, int lastDay, bool allowed)
	{
		using var fixture = new TickFixture(maxDays: maxDays);
		var task = fixture.Ticks(Utc(10, firstDay), Utc(10, lastDay + 1));
		if (allowed)
		{
			await task;
			return;
		}

		var error = Assert.ThrowsAsync<TInvestRequestException>(() => task);
		Assert.That(error!.Message, Does.Contain("UTC days").And.Contain($"the limit is {maxDays}"));
		Assert.That(fixture.Gateway.ArchiveRequests, Is.Empty);
	}

	[Test]
	public void ThirtyOneDaysPassThePlanCapAtTheDefaultAndOneMoreDoesNot()
	{
		using var fixture = new TickFixture();
		// A cold 31-day range passes the day cap but needs more archives than one window holds: retry-in, not a cap reject.
		var cold = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc), Utc(10, 29)));
		Assert.That(cold!.Message, Does.Contain("retry in").And.Not.Contain("UTC days"));
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc), Utc(10, 29)));
		Assert.That(error!.Message, Does.Contain("32 UTC days"));
	}

	[TestCase(1000, true)]
	[TestCase(999, false)]
	public async Task TickCapRejectsBeforeTheHeaderAndAllowsTheCapItself(int lessThan, bool allowed)
	{
		using var fixture = new TickFixture(maxTicks: 1000);
		var rows = Enumerable.Range(0, 1000).Select(i => Row("2026-10-28T07:00:00Z", "BUY", "1", 1)).ToArray();
		fixture.Archive(10, 28, Gzip(Csv(rows)));
		var end = allowed ? Utc(10, 29) : Utc(10, 29);
		if (allowed)
		{
			Assert.That((await fixture.Ticks(Utc(10, 28), end)).Total, Is.EqualTo(1000));
			return;
		}

		fixture.Archive(10, 28, Gzip(Csv(rows.Append(Row("2026-10-28T07:00:00Z", "BUY", "1", 1)).ToArray())));
		fixture.Store.ArchiveCache.Delete(fixture.Store.ArchiveCache.PathFor("invented-uid-SYNTH", new DateOnly(2026, 10, 28)));
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 28), end));
		Assert.That(error!.Message, Does.Contain("1001 ticks").And.Contain("the limit is 1000"));
		_ = lessThan;
	}

	[Test]
	public async Task DealerRowsDoNotCountTowardTheCap()
	{
		using var fixture = new TickFixture(maxTicks: 1000);
		var rows = Enumerable.Range(0, 1000).Select(_ => Row("2026-10-28T07:00:00Z", "BUY", "1", 1)).Concat(Enumerable.Range(0, 500).Select(_ => Row("2026-10-28T07:00:01Z", "BUY", "1", 1, "DEALER"))).ToArray();
		fixture.Archive(10, 28, Gzip(Csv(rows)));
		Assert.That((await fixture.Ticks(Utc(10, 28), Utc(10, 29))).Total, Is.EqualTo(1000));
	}

	[Test]
	public async Task BatchesFollowTheBatchSizeAndTheLastBatchHasNoMore()
	{
		using var fixture = new TickFixture();
		var rows = Enumerable.Range(0, TInvestTickReader.BatchSize + 5).Select(i => Row("2026-10-28T07:00:00Z", "BUY", (i + 1).ToString(), 1)).ToArray();
		fixture.Archive(10, 28, Gzip(Csv(rows)));
		var reader = await fixture.Ticks(Utc(10, 28), Utc(10, 29));
		var first = await reader.ReadSymbolHistoryAsync();
		Assert.That(first.Records, Has.Count.EqualTo(TInvestTickReader.BatchSize));
		Assert.That(first.HasMore, Is.True);
		var last = await reader.ReadSymbolHistoryAsync();
		Assert.That(last.Records, Has.Count.EqualTo(5));
		Assert.That(last.HasMore, Is.False);
	}
}

[TestFixture]
public class TickCacheAndMissingDayTests
{
	[Test]
	public async Task SecondRequestComesFromTheCacheAndADamagedFileDownloadsAgain()
	{
		using var fixture = new TickFixture();
		fixture.Archive(10, 28, Gzip(Csv(Row("2026-10-28T07:00:00Z", "BUY", "1", 1))));
		await ReadAll(await fixture.Ticks(Utc(10, 28), Utc(10, 29)));
		await ReadAll(await fixture.Ticks(Utc(10, 28), Utc(10, 29)));
		Assert.That(fixture.Gateway.ArchiveRequests, Has.Count.EqualTo(1));
		var path = fixture.Store.ArchiveCache.PathFor("invented-uid-SYNTH", new DateOnly(2026, 10, 28));
		Assert.That(File.Exists(path), Is.True);
		Assert.That(path, Does.StartWith(fixture.Folder));
		File.WriteAllText(path, "damaged");
		Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 28), Utc(10, 29)));
		Assert.That(File.Exists(path), Is.False);
		Assert.That(await ReadAll(await fixture.Ticks(Utc(10, 28), Utc(10, 29))), Has.Count.EqualTo(1));
		Assert.That(fixture.Gateway.ArchiveRequests, Has.Count.EqualTo(2));
	}

	[Test]
	public async Task AnArchiveRecordsTheNewestArchivedDayForTheRecorder()
	{
		using var fixture = new TickFixture();
		fixture.Archive(10, 28, Gzip(Csv(Row("2026-10-28T07:00:00Z", "BUY", "1", 1))));
		await fixture.Ticks(Utc(10, 28), Utc(10, 29));
		Assert.That(fixture.Recording.DeletedBefore, Is.EqualTo(new DateOnly(2026, 10, 28)));
	}

	[TestCase(25, "Sunday")]
	[TestCase(28, "Wednesday")]
	public async Task NoArchiveAndNoExchangeCandlesAfterCandleCoverageMeansNoTradingEvenOnAWeekday(int day, string name)
	{
		using var fixture = new TickFixture();
		var reader = await fixture.Ticks(Utc(10, day), Utc(10, day + 1));
		Assert.That(reader.Total, Is.EqualTo(0), name);
		Assert.That(fixture.Gateway.CandleRequests.Single().IntervalSeconds, Is.EqualTo(60));
		Assert.That(fixture.Gateway.CandleRequests.Single().IncludeDealer, Is.False);
		// The proof is remembered: no second candle call.
		await fixture.Ticks(Utc(10, day), Utc(10, day + 1));
		Assert.That(fixture.Gateway.CandleRequests, Has.Count.EqualTo(1));
	}

	[TestCase(25)]
	[TestCase(28)]
	public void NoArchiveWhileExchangeCandlesExistRejectsWeekendsAndWeekdaysAlike(int day)
	{
		using var fixture = new TickFixture();
		fixture.Gateway.Candles = (request, _) => Task.FromResult<IReadOnlyList<TInvestCandle>>(new[] { new TInvestCandle(request.Start.AddHours(10), 1, 1, 1, 1, 1, true, true) });
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, day), Utc(10, day + 1)));
		Assert.That(error!.Message, Is.EqualTo($"no T-Invest trade archive for 2026-10-{day}"));
	}

	[Test]
	public async Task DealerOnlyCandlesDoNotProveTrading()
	{
		using var fixture = new TickFixture();
		fixture.Gateway.Candles = (request, _) => Task.FromResult<IReadOnlyList<TInvestCandle>>(new[] { new TInvestCandle(request.Start.AddHours(10), 1, 1, 1, 1, 1, true, false) });
		Assert.That((await fixture.Ticks(Utc(10, 25), Utc(10, 26))).Total, Is.EqualTo(0));
	}

	[Test]
	public void ADayBeforeCandleCoverageCannotBeProvenEmpty()
	{
		using var fixture = new TickFixture(firstCandle: new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero));
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 25), Utc(10, 26)));
		Assert.That(error!.Message, Does.Contain("no T-Invest trade archive for 2026-10-25"));
	}

	[Test]
	public async Task AMissingYesterdayIsServedFromTheRecorderWhenItCoversTheDay()
	{
		using var fixture = new TickFixture();
		fixture.Gateway.Candles = (request, _) => Task.FromResult<IReadOnlyList<TInvestCandle>>(new[] { new TInvestCandle(request.Start.AddHours(10), 1, 1, 1, 1, 1, true, true) });
		fixture.Recording.Intervals.Add((new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero)));
		fixture.Recording.Trades.Add(Trade(Utc(10, 29, 10), 0, 5, 2));
		Assert.That((await fixture.Ticks(Utc(10, 29), Utc(10, 30))).Total, Is.EqualTo(1));
		fixture.Recording.Intervals.Clear();
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(Utc(10, 29), Utc(10, 30)));
		Assert.That(error!.Message, Does.Contain("no T-Invest trade archive for 2026-10-29"));
	}

	[Test]
	public void CacheEvictsTheLeastRecentlyUsedFilesAndKeepsTheNewOne()
	{
		var clock = new ManualTimeProvider();
		var folder = Path.Combine(Path.GetTempPath(), "sdp-cache-" + Guid.NewGuid().ToString("N"));
		try
		{
			var cache = new TradeArchiveCache(folder, 25, clock);
			var days = new[] { 1, 2, 3 }.Select(d => new DateOnly(2026, 10, d)).ToArray();
			foreach (var day in days)
			{
				var path = cache.PathFor("invented-uid", day);
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllBytes(path, new byte[10]);
				File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime);
				clock.Advance(TimeSpan.FromMinutes(1));
			}

			Assert.That(cache.TryGet("invented-uid", days[0], out _), Is.True, "A hit refreshes the file.");
			cache.Evict(cache.PathFor("invented-uid", days[2]));
			Assert.That(cache.TryGet("invented-uid", days[0], out _), Is.True);
			Assert.That(cache.TryGet("invented-uid", days[1], out _), Is.False, "The oldest unused file went first.");
			Assert.That(cache.TryGet("invented-uid", days[2], out _), Is.True);
			Assert.That(cache.NewestDay(), Is.EqualTo(days[2]));
		}
		finally
		{
			if (Directory.Exists(folder)) Directory.Delete(folder, true);
		}
	}

	[Test]
	public void UnsafeInstrumentIdsAreHashedIntoASingleFolderName()
	{
		var cache = new TradeArchiveCache(Path.Combine(Path.GetTempPath(), "sdp-cache-x"), 100, new ManualTimeProvider());
		var path = cache.PathFor("../..\\evil", new DateOnly(2026, 10, 1));
		Assert.That(Path.GetRelativePath(cache.Folder, path).Split(Path.DirectorySeparatorChar), Has.Length.EqualTo(2));
		Assert.That(path, Does.Not.Contain(".."));
	}
}
