namespace SomeDataProvider.DataStorage.Fred;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Only category children and one live series offset page. Never observations or a recursive crawl.</summary>
public interface IFredCatalogGateway
{
	Task<IReadOnlyList<FredCatalogCategory>> GetChildrenAsync(int categoryId, CancellationToken ct);
	Task<FredCatalogSeriesPage> GetSeriesPageAsync(int categoryId, int offset, int limit, CancellationToken ct);
}

public sealed record FredCatalogCategory(int Id, int ParentId, string Name);
public sealed record FredCatalogSeries(string Id, string Title);
public sealed record FredCatalogSeriesPage(int Offset, int Limit, int? Total, IReadOnlyList<FredCatalogSeries> Rows);

public sealed class FredCatalogException(string reason) : Exception("The FRED catalog could not be read.")
{
	public string Reason { get; } = reason;
}
