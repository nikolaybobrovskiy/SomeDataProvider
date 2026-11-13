namespace SomeDataProvider.DtcProtocolServer.Data;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Protocol.Private;
using SomeDataProvider.DataStorage.Fred;

/// <summary>A bounded local offset view, not an upstream snapshot. No prefetch or recursive category walk.</summary>
sealed class FredCatalog(IFredCatalogGateway gateway, TimeProvider clock) : IDisposable
{
	internal const int MaxViews = 16;
	internal const int MaxPagesPerView = 128;
	internal const int MaxCachedRows = 50000;
	internal const int MaxDepth = 16;
	internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

	readonly SemaphoreSlim _gate = new(1, 1);
	readonly Dictionary<ViewKey, View> _views = new();

	public void Dispose() => _gate.Dispose();

	internal async Task<IReadOnlyList<ProviderInformationMessage>> ReadAsync(SymbolCatalogRequest request, CancellationToken ct)
	{
		if (request.Catalog != "fred" || request.PageSize is < 1 or > 500) throw new FredCatalogException("InvalidRequest");
		var ancestors = ParseNode(request.NodeID);
		if (ancestors.Length >= MaxDepth) throw new FredCatalogException("LimitRejected");
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			foreach (var expired in _views.Where(p => clock.GetElapsedTime(p.Value.Started) >= Lifetime).Select(p => p.Key).ToArray()) _views.Remove(expired);
			var key = new ViewKey(request.NodeID, request.Filter, request.PageSize);
			_views.TryGetValue(key, out var view);
			var pageIndex = 0;
			if (request.Cursor != null)
			{
				var generation = request.Cursor.Split('.')[0];
				var cursorView = _views.Values.FirstOrDefault(v => v.Version == generation);
				if (cursorView == null) throw new FredCatalogException("SnapshotExpired");
				if (cursorView != view || !cursorView.Cursors.TryGetValue(request.Cursor, out pageIndex)) throw new FredCatalogException("InvalidCursor");
			}
			if (request.SnapshotVersion != null && request.SnapshotVersion != view?.Version) throw new FredCatalogException("SnapshotExpired");
			if (view == null)
			{
				if (_views.Count >= MaxViews) _views.Remove(_views.MinBy(p => p.Value.Started).Key);
				view = new View(clock.GetTimestamp());
				_views.Add(key, view);
			}
			if (view.Pages.TryGetValue(pageIndex, out var cached)) return Bind(cached, request.RequestID);
			if (pageIndex != view.Pages.Count) throw new FredCatalogException("InvalidCursor");
			if (pageIndex >= MaxPagesPerView) throw new FredCatalogException("LimitRejected");
			try
			{
				var rows = new List<ProviderInformationMessage>();
				var hasMore = false;
				int? total = null;
				if (request.NodeID.Length == 0)
				{
					if (Matches("FRED", request.Filter)) rows.Add(new SymbolCatalogNode { Catalog = "fred", NodeID = "fred", Name = "FRED", SnapshotVersion = view.Version });
					total = rows.Count;
				}
				else
				{
					var category = ancestors[^1];
					if (view.Children == null)
					{
						var children = await gateway.GetChildrenAsync(category, ct).ConfigureAwait(false);
						ct.ThrowIfCancellationRequested();
						if (children.Count > Service.MaxCatalogChildren) throw new FredCatalogException("InvalidSourceResponse");
						foreach (var child in children)
						{
							if (child.Id < 0 || child.ParentId != category) throw new FredCatalogException("InvalidSourceResponse");
							CheckText(child.Name, 1024);
						}
						view.Children = children.Where(c => !ancestors.Contains(c.Id)).DistinctBy(c => c.Id).OrderBy(c => c.Id).ToArray();
					}
					if (view.ChildOffset < view.Children.Length)
					{
						foreach (var child in view.Children.Skip(view.ChildOffset).Take(request.PageSize))
						{
							if (Matches(child.Name, request.Filter))
							{
								rows.Add(new SymbolCatalogNode
								{
									Catalog = "fred", NodeID = request.NodeID + "/" + child.Id.ToString(CultureInfo.InvariantCulture), Name = child.Name,
									ParentNodeID = request.NodeID, SnapshotVersion = view.Version,
								});
							}
						}
						view.ChildOffset += Math.Min(request.PageSize, view.Children.Length - view.ChildOffset);
						hasMore = true; // The first series page is a separate explicit request, even after the final child.
					}
					else
					{
						var page = await gateway.GetSeriesPageAsync(category, view.SeriesOffset, request.PageSize, ct).ConfigureAwait(false);
						ct.ThrowIfCancellationRequested();
						if (page.Offset != view.SeriesOffset || page.Limit != request.PageSize || page.Rows.Count > request.PageSize || page.Total < 0)
							throw new FredCatalogException("InvalidSourceResponse");
						if (view.Total != null && page.Total != null && page.Total != view.Total) throw new FredCatalogException("SnapshotExpired");
						var last = view.LastSeries;
						foreach (var series in page.Rows)
						{
							CheckText(series.Title, 1024);
							if (string.IsNullOrEmpty(series.Id) || series.Id.Length > 59 || series.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
								throw new FredCatalogException("InvalidSourceResponse");
							if (last != null && string.CompareOrdinal(series.Id, last) < 0) throw new FredCatalogException("SnapshotExpired");
							last = series.Id;
						}
						foreach (var series in page.Rows)
						{
							if (view.Seen.Add(series.Id) && (Matches(series.Id, request.Filter) || Matches(series.Title, request.Filter)))
							{
								rows.Add(new SymbolCatalogEntry
								{
									Catalog = "fred", NodeID = request.NodeID, Symbol = "fred-" + series.Id, Name = series.Title, SnapshotVersion = view.Version,
								});
							}
						}
						view.LastSeries = last;
						if (page.Total != null) view.Total = page.Total;
						view.SeriesOffset += page.Rows.Count; // Consumed raw rows, not filtered/deduplicated output.
						hasMore = page.Rows.Count > 0 && (page.Total is { } sourceTotal ? view.SeriesOffset < sourceTotal : page.Rows.Count == request.PageSize);
					}
				}
				if (clock.GetElapsedTime(view.Started) >= Lifetime) throw new FredCatalogException("SnapshotExpired");
				// Evict whole other views before retaining more rows; their cursors expire, never change meaning.
				while (_views.Sum(p => p.Value.Weight) + rows.Count + 1 > MaxCachedRows && _views.Count > 1)
				{
					_views.Remove(_views.Where(p => p.Key != key).MinBy(p => p.Value.Started).Key);
				}
				if (_views.Sum(p => p.Value.Weight) + rows.Count + 1 > MaxCachedRows) throw new FredCatalogException("LimitRejected");
				string? next = null;
				if (hasMore)
				{
					next = view.Version + "." + NewID();
					view.Cursors.Add(next, pageIndex + 1);
				}
				for (var i = 0; i < rows.Count; i++)
				{
					rows[i] = rows[i] switch
					{
						SymbolCatalogNode n => n with { RowIndex = i, RowsInPage = rows.Count },
						SymbolCatalogEntry e => e with { RowIndex = i, RowsInPage = rows.Count },
						_ => throw new InvalidOperationException(),
					};
				}
				rows.Add(new SymbolCatalogEnd
				{
					Catalog = "fred", NodeID = request.NodeID, RowCount = rows.Count, SnapshotVersion = view.Version, NextCursor = next,
					Total = total, Consistency = 2, // BestEffortOffsetView. A source end is not proof of an exhaustive inventory.
				});
				var result = rows.ToArray();
				view.Pages.Add(pageIndex, result);
				return Bind(result, request.RequestID);
			}
			catch
			{
				_views.Remove(key); // Never resume from partially changed offsets/dedup state after failure or cancellation.
				throw;
			}
		}
		finally { _gate.Release(); }
	}

	static ProviderInformationMessage[] Bind(IEnumerable<ProviderInformationMessage> rows, int requestID) => rows.Select(r => r with { RequestID = requestID }).ToArray();
	static string NewID() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
	static bool Matches(string value, string filter) => value.Contains(filter, StringComparison.OrdinalIgnoreCase);

	static void CheckText(string value, int maxBytes)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || Encoding.UTF8.GetByteCount(value) > maxBytes) throw new FredCatalogException("InvalidSourceResponse");
	}

	static int[] ParseNode(string node)
	{
		if (node.Length == 0 || node == "fred") return new[] { 0 };
		var parts = node.Split('/');
		if (parts[0] != "fred" || parts.Length > MaxDepth) throw new FredCatalogException("InvalidNode");
		var ids = new List<int> { 0 };
		foreach (var part in parts.Skip(1))
		{
			if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || id.ToString(CultureInfo.InvariantCulture) != part || ids.Contains(id))
				throw new FredCatalogException("InvalidNode");
			ids.Add(id);
		}
		return ids.ToArray();
	}

	readonly record struct ViewKey(string Node, string Filter, int PageSize);
	sealed class View(long started)
	{
		internal long Started { get; } = started;
		internal string Version { get; } = NewID();
		internal Dictionary<int, ProviderInformationMessage[]> Pages { get; } = new();
		internal Dictionary<string, int> Cursors { get; } = new(StringComparer.Ordinal);
		internal HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
		internal FredCatalogCategory[]? Children { get; set; }
		internal int ChildOffset { get; set; }
		internal int SeriesOffset { get; set; }
		internal int? Total { get; set; }
		internal string? LastSeries { get; set; }
		internal int Weight => (Children?.Length ?? 0) + Seen.Count + Pages.Values.Sum(p => p.Length);
	}
}
