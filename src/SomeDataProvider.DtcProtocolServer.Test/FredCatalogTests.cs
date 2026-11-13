namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Protocol.Private;
using NUnit.Framework;
using SomeDataProvider.DataStorage.Fred;
using SomeDataProvider.DtcProtocolServer.Data;

[TestFixture]
public class FredCatalogTests
{
	static readonly SymbolCatalogRequest Request = new() { Catalog = "fred", NodeID = "fred", PageSize = 2 };

	[TestCase(5000, 200)]
	[TestCase(5000, 500)]
	[TestCase(20000, 200)]
	[TestCase(20000, 500)]
	public async Task BoundedPagesReachLargeListingsWithoutHydrationOrPrefetch(int count, int size)
	{
		var gateway = new FakeGateway(count);
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		var request = Request with { PageSize = size };
		var seen = new HashSet<string>();
		var pages = 0;
		do
		{
			var page = await catalog.ReadAsync(request, default);
			var end = End(page);
			Assert.That(end.Consistency, Is.EqualTo(2));
			Assert.That(end.Total, Is.Null);
			Assert.That(end.RowCount, Is.EqualTo(size));
			for (var i = 0; i < size; i++)
			{
				var row = (SymbolCatalogEntry)page[i];
				Assert.That(row.RowIndex, Is.EqualTo(i));
				Assert.That(row.RowsInPage, Is.EqualTo(size));
				Assert.That(row.SnapshotVersion, Is.EqualTo(end.SnapshotVersion));
				Assert.That(seen.Add(row.Symbol), Is.True);
			}
			request = request with { Cursor = end.NextCursor, SnapshotVersion = end.SnapshotVersion };
			pages++;
		}
		while (request.Cursor != null && pages <= count / size);
		Assert.That(seen.Count, Is.EqualTo(count));
		Assert.That(gateway.ChildrenCalls, Is.EqualTo(new[] { 0 }));
		Assert.That(gateway.SeriesCalls.Select(c => c.Offset), Is.EqualTo(Enumerable.Range(0, count / size).Select(i => i * size)));
		Assert.That(gateway.SeriesCalls.All(c => c.Limit == size), Is.True);
	}

	[TestCase(true)]
	[TestCase(false)]
	public async Task SameCountInsertionDeletionIsOnlyABestEffortView(bool insertionBeforeCursor)
	{
		var gateway = new FakeGateway { Series = Series("SYNTHB", "SYNTHD", "SYNTHF", "SYNTHH") };
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		var first = await catalog.ReadAsync(Request, default);
		gateway.Series = insertionBeforeCursor ? Series("SYNTHA", "SYNTHB", "SYNTHD", "SYNTHF") : Series("SYNTHD", "SYNTHF", "SYNTHH", "SYNTHZ");
		var second = await catalog.ReadAsync(Next(Request, first), default);
		Assert.That(End(second).SnapshotVersion, Is.EqualTo(End(first).SnapshotVersion));
		Assert.That(End(second).Consistency, Is.EqualTo(2));
		Assert.That(End(second).NextCursor, Is.Null);
		Assert.That(End(second).Total, Is.Null);
		var combined = first.Concat(second).OfType<SymbolCatalogEntry>().Select(r => r.Symbol).ToArray();
		Assert.That(combined.Distinct().Count(), Is.EqualTo(combined.Length));
		Assert.That(combined, Does.Not.Contain(insertionBeforeCursor ? "fred-SYNTHA" : "fred-SYNTHF"), "A same-count offset shift may skip a series. Do not claim a complete snapshot.");
		Assert.That(gateway.SeriesCalls.Select(c => c.Offset), Is.EqualTo(new[] { 0, 2 }));
		var replay = await catalog.ReadAsync(Request with { RequestID = 81 }, default);
		Assert.That(replay.All(r => r.RequestID == 81), Is.True);
		Assert.That(gateway.SeriesCalls.Count, Is.EqualTo(2));
	}

	[TestCase("count")]
	[TestCase("order")]
	public async Task ObservedChangesInvalidateRatherThanMixLocalGenerations(string change)
	{
		var gateway = new FakeGateway { Series = Series("SYNTHB", "SYNTHD", "SYNTHF", "SYNTHH") };
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		var first = await catalog.ReadAsync(Request, default);
		gateway.Series = change == "count" ? Series("SYNTHB", "SYNTHD", "SYNTHF") : Series("SYNTHB", "SYNTHD", "SYNTHB", "SYNTHF");
		Reject(catalog, Next(Request, first), "SnapshotExpired");
		Reject(catalog, Next(Request, first), "SnapshotExpired");
		Assert.That(gateway.SeriesCalls.Count, Is.EqualTo(2));
		var refreshed = await catalog.ReadAsync(Request, default);
		Assert.That(End(refreshed).SnapshotVersion, Is.Not.EqualTo(End(first).SnapshotVersion));
	}

	[Test]
	public async Task ExpiryIsMonotonicAndRejectsAnOldCursorBeforeAnySourceCall()
	{
		var gateway = new FakeGateway(5);
		var clock = new ManualTimeProvider();
		using var catalog = new FredCatalog(gateway, clock);
		var first = await catalog.ReadAsync(Request, default);
		clock.Advance(FredCatalog.Lifetime - TimeSpan.FromSeconds(1));
		await catalog.ReadAsync(Request, default);
		Assert.That(gateway.SeriesCalls.Count, Is.EqualTo(1));
		clock.Advance(TimeSpan.FromSeconds(1));
		Reject(catalog, Next(Request, first), "SnapshotExpired");
		Assert.That(gateway.SeriesCalls.Count, Is.EqualTo(1));
		Assert.That(End(await catalog.ReadAsync(Request, default)).SnapshotVersion, Is.Not.EqualTo(End(first).SnapshotVersion));
	}

	[Test]
	public async Task CyclesAndDuplicateChildIDsCannotGrowAnInfinitePath()
	{
		var gateway = new FakeGateway();
		gateway.Children = id => id == 0
			? new[] { new FredCatalogCategory(0, 0, "Invented cycle"), new FredCatalogCategory(17, 0, "Invented category"), new FredCatalogCategory(17, 0, "Duplicate") }
			: new[] { new FredCatalogCategory(0, 17, "Back to root"), new FredCatalogCategory(17, 17, "Self"), new FredCatalogCategory(29, 17, "Invented child") };
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		Assert.That((await catalog.ReadAsync(Request, default)).OfType<SymbolCatalogNode>().Select(r => r.NodeID), Is.EqualTo(new[] { "fred/17" }));
		Assert.That((await catalog.ReadAsync(Request with { NodeID = "fred/17" }, default)).OfType<SymbolCatalogNode>().Select(r => r.NodeID), Is.EqualTo(new[] { "fred/17/29" }));
		Reject(catalog, Request with { NodeID = "fred/17/17" }, "InvalidNode");
		Reject(catalog, Request with { NodeID = "fred/17/0" }, "InvalidNode");
		Assert.That(gateway.ChildrenCalls, Is.EqualTo(new[] { 0, 17 }));
		Assert.That(gateway.SeriesCalls, Is.Empty);
	}

	[Test]
	public async Task UnknownTotalsStayAbsentThroughTheEndAndDuplicateRowsDoNotRefillPages()
	{
		var gateway = new FakeGateway { Series = Series("SYNTHA", "SYNTHA", "SYNTHB", "SYNTHB"), UnknownTotal = true };
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		var first = await catalog.ReadAsync(Request, default);
		Assert.That(End(first).RowCount, Is.EqualTo(1));
		var second = await catalog.ReadAsync(Next(Request, first), default);
		Assert.That(End(second).RowCount, Is.EqualTo(1));
		var last = await catalog.ReadAsync(Next(Request, second), default);
		Assert.That(End(last).NextCursor, Is.Null);
		Assert.That(End(last).RowCount, Is.Zero);
		Assert.That(new[] { first, second, last }.All(p => End(p).Total == null), Is.True);
		Assert.That(gateway.SeriesCalls.Select(c => c.Offset), Is.EqualTo(new[] { 0, 2, 4 }));
	}

	[Test]
	public async Task FilterOnlyUsesTheRequestedRawPageAndNeverSearchesAhead()
	{
		var gateway = new FakeGateway(5);
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		var request = Request with { Filter = "SYNTH00004" };
		var first = await catalog.ReadAsync(request, default);
		Assert.That(End(first).RowCount, Is.Zero);
		Assert.That(End(first).NextCursor, Is.Not.Null);
		Assert.That(gateway.SeriesCalls.Count, Is.EqualTo(1));
		await catalog.ReadAsync(Next(request, first), default);
		Assert.That(gateway.SeriesCalls.Select(c => c.Offset), Is.EqualTo(new[] { 0, 2 }));
	}

	[TestCase("filter")]
	[TestCase("node")]
	[TestCase("size")]
	[TestCase("version")]
	[TestCase("tamper")]
	public async Task CursorsBindEveryViewInputAndCannotForgeAnOffset(string change)
	{
		var gateway = new FakeGateway(5);
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		var next = Next(Request, await catalog.ReadAsync(Request, default));
		next = change switch
		{
			"filter" => next with { Filter = "invented" }, "node" => next with { NodeID = "fred/17" },
			"size" => next with { PageSize = 3 }, "version" => next with { SnapshotVersion = "invented-version" },
			_ => next with { Cursor = next.Cursor + "x" },
		};
		Reject(catalog, next, change == "version" ? "SnapshotExpired" : "InvalidCursor");
		Assert.That(gateway.ChildrenCalls.Count, Is.EqualTo(1));
		Assert.That(gateway.SeriesCalls.Count, Is.EqualTo(1));
	}

	[TestCase(0)]
	[TestCase(501)]
	public void InvalidPageSizesNeverReachTheGateway(int size)
	{
		var gateway = new FakeGateway(5);
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		Reject(catalog, Request with { PageSize = size }, "InvalidRequest");
		Assert.That(gateway.ChildrenCalls, Is.Empty);
		Assert.That(gateway.SeriesCalls, Is.Empty);
	}

	[Test]
	public async Task ThePageBudgetRejectsBeforeTheNextCallRatherThanCrawling()
	{
		var gateway = new FakeGateway(300);
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		var request = Request with { PageSize = 1 };
		for (var i = 0; i < FredCatalog.MaxPagesPerView; i++) request = Next(request, await catalog.ReadAsync(request, default));
		Reject(catalog, request, "LimitRejected");
		Assert.That(gateway.SeriesCalls.Count, Is.EqualTo(FredCatalog.MaxPagesPerView));
	}

	[Test]
	public async Task BoundedViewEvictionExpiresTheOldCursorWithoutARefetch()
	{
		var gateway = new FakeGateway(5);
		var clock = new ManualTimeProvider();
		using var catalog = new FredCatalog(gateway, clock);
		var first = await catalog.ReadAsync(Request, default);
		for (var i = 0; i < FredCatalog.MaxViews; i++)
		{
			clock.Advance(TimeSpan.FromSeconds(1));
			await catalog.ReadAsync(Request with { NodeID = string.Empty, Filter = "invented" + i }, default);
		}
		Reject(catalog, Next(Request, first), "SnapshotExpired");
		Assert.That(gateway.SeriesCalls.Count, Is.EqualTo(1));
	}

	[TestCase(16, "LimitRejected")]
	[TestCase(17, "InvalidNode")]
	public void DepthAdmissionRejectsBeforeAnyCategoryOrSeriesCall(int depth, string reason)
	{
		var gateway = new FakeGateway(5)
		{
			Children = id => new[] { new FredCatalogCategory(depth, id, "Invented deeper child") },
		};
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		var node = "fred/" + string.Join("/", Enumerable.Range(1, depth - 1));
		Reject(catalog, Request with { NodeID = node }, reason);
		Reject(catalog, Request with { NodeID = node }, reason);
		Assert.That(gateway.ChildrenCalls, Is.Empty);
		Assert.That(gateway.SeriesCalls, Is.Empty);
	}

	[Test]
	public async Task ConcurrentReadersShareTheSameExplicitPageLoad()
	{
		var source = new TaskCompletionSource<FredCatalogSeriesPage>(TaskCreationOptions.RunContinuationsAsynchronously);
		var gateway = new FakeGateway(5) { Response = (_, _, _) => source.Task };
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		var readers = Enumerable.Range(1, 4).Select(id => catalog.ReadAsync(Request with { RequestID = id }, default)).ToArray();
		Assert.That(gateway.SeriesCalls.Count, Is.EqualTo(1));
		source.SetResult(new FredCatalogSeriesPage(0, 2, 5, gateway.Series.Take(2).ToArray()));
		var pages = await Task.WhenAll(readers);
		Assert.That(gateway.ChildrenCalls.Count, Is.EqualTo(1));
		Assert.That(gateway.SeriesCalls.Count, Is.EqualTo(1));
		for (var i = 0; i < pages.Length; i++) Assert.That(pages[i].All(r => r.RequestID == i + 1), Is.True);
	}

	[Test]
	public async Task ExpiryDuringSourceWorkDoesNotPublishAnAlreadyExpiredGeneration()
	{
		var clock = new ManualTimeProvider();
		var gateway = new FakeGateway(5) { BeforeSeries = () => clock.Advance(FredCatalog.Lifetime) };
		using var catalog = new FredCatalog(gateway, clock);
		Reject(catalog, Request, "SnapshotExpired");
		gateway.BeforeSeries = null;
		var refreshed = await catalog.ReadAsync(Request, default);
		Assert.That(End(refreshed).RowCount, Is.EqualTo(2));
		Assert.That(gateway.SeriesCalls.Select(c => c.Offset), Is.EqualTo(new[] { 0, 0 }));
	}

	[Test]
	public async Task SourceFailureDiscardsPartiallyBuiltOffsetsAndDedupState()
	{
		var gateway = new FakeGateway(5);
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		var first = await catalog.ReadAsync(Request, default);
		gateway.BeforeSeries = () => throw new FredCatalogException("SourceUnavailable");
		Reject(catalog, Next(Request, first), "SourceUnavailable");
		gateway.BeforeSeries = null;
		Reject(catalog, Next(Request, first), "SnapshotExpired");
		var refreshed = await catalog.ReadAsync(Request, default);
		Assert.That(End(refreshed).RowCount, Is.EqualTo(2));
		Assert.That(End(refreshed).SnapshotVersion, Is.Not.EqualTo(End(first).SnapshotVersion));
		Assert.That(gateway.SeriesCalls.Select(c => c.Offset), Is.EqualTo(new[] { 0, 2, 0 }));
	}

	[Test]
	public async Task CancellationAfterSourceDispatchDoesNotPublishOrRetainAnAbortedView()
	{
		using var cancel = new CancellationTokenSource();
		var gateway = new FakeGateway(5) { BeforeSeries = cancel.Cancel };
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		Assert.That(async () => await catalog.ReadAsync(Request, cancel.Token), Throws.InstanceOf<OperationCanceledException>());
		gateway.BeforeSeries = null;
		Assert.That(End(await catalog.ReadAsync(Request, default)).RowCount, Is.EqualTo(2));
		Assert.That(gateway.SeriesCalls.Select(c => c.Offset), Is.EqualTo(new[] { 0, 0 }));
	}

	[Test]
	public async Task KnownCountEvidenceIsRetainedAcrossAPageWithoutCount()
	{
		var gateway = new FakeGateway(6);
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		var first = await catalog.ReadAsync(Request, default);
		gateway.UnknownTotal = true;
		var second = await catalog.ReadAsync(Next(Request, first), default);
		gateway.UnknownTotal = false;
		gateway.Series = gateway.Series.Take(5).ToArray();
		Reject(catalog, Next(Request, second), "SnapshotExpired");
	}

	[Test]
	public async Task DuplicatePathsKeepTheSameCanonicalSeriesIdentity()
	{
		var gateway = new FakeGateway(1);
		using var catalog = new FredCatalog(gateway, new ManualTimeProvider());
		var first = await catalog.ReadAsync(Request with { NodeID = "fred/17" }, default);
		var second = await catalog.ReadAsync(Request with { NodeID = "fred/29" }, default);
		Assert.That(first.OfType<SymbolCatalogEntry>().Single().Symbol, Is.EqualTo(second.OfType<SymbolCatalogEntry>().Single().Symbol));
		Assert.That(gateway.SeriesCalls.Select(c => c.Category), Is.EqualTo(new[] { 17, 29 }));
	}

	static SymbolCatalogRequest Next(SymbolCatalogRequest request, IReadOnlyList<ProviderInformationMessage> page) => request with { Cursor = End(page).NextCursor, SnapshotVersion = End(page).SnapshotVersion };
	static SymbolCatalogEnd End(IReadOnlyList<ProviderInformationMessage> page) => (SymbolCatalogEnd)page[^1];
	static FredCatalogSeries[] Series(params string[] ids) => ids.Select(id => new FredCatalogSeries(id, "Invented " + id)).ToArray();
	static void Reject(FredCatalog catalog, SymbolCatalogRequest request, string reason) =>
		Assert.That(async () => await catalog.ReadAsync(request, default), Throws.TypeOf<FredCatalogException>().With.Property(nameof(FredCatalogException.Reason)).EqualTo(reason));

	sealed class FakeGateway : IFredCatalogGateway
	{
		public FakeGateway(int count = 0) => Series = Enumerable.Range(0, count).Select(i => new FredCatalogSeries("SYNTH" + i.ToString("D5"), "Invented series " + i)).ToArray();
		public List<int> ChildrenCalls { get; } = new();
		public List<(int Category, int Offset, int Limit)> SeriesCalls { get; } = new();
		public FredCatalogSeries[] Series { get; set; }
		public bool UnknownTotal { get; set; }
		public Action? BeforeSeries { get; set; }
		public Func<int, int, int, Task<FredCatalogSeriesPage>>? Response { get; set; }
		public Func<int, FredCatalogCategory[]> Children { get; set; } = _ => Array.Empty<FredCatalogCategory>();

		public Task<IReadOnlyList<FredCatalogCategory>> GetChildrenAsync(int categoryId, CancellationToken ct)
		{
			ChildrenCalls.Add(categoryId);
			return Task.FromResult<IReadOnlyList<FredCatalogCategory>>(Children(categoryId));
		}

		public Task<FredCatalogSeriesPage> GetSeriesPageAsync(int categoryId, int offset, int limit, CancellationToken ct)
		{
			SeriesCalls.Add((categoryId, offset, limit));
			BeforeSeries?.Invoke();
			if (Response != null) return Response(categoryId, offset, limit);
			return Task.FromResult(new FredCatalogSeriesPage(offset, limit, UnknownTotal ? null : Series.Length, Series.Skip(offset).Take(limit).ToArray()));
		}
	}
}
