namespace SomeDataProvider.DataStorage.Definitions
{
	using System;
	using System.Threading;
	using System.Threading.Tasks;

	public interface ISymbolHistoryStoreCache : ISymbolHistoryStore
	{
		Task<ISymbolHistoryStoreCacheEntry?> GetSymbolHistoryStoreCacheEntry(ISymbol symbol, CancellationToken cancellationToken = default);
	}

	public interface ISymbolHistoryStoreCacheEntry
	{
		public DateTime CachedPeriodStart { get; } // As requested.

		public DateTime CachedPeriodEnd { get; } // As requested, can be infinity.

		public ETag ETag { get; } // Like last update/release date. Cannot be null. Otherwise cache has no sense.

		public DateTime RevisablePeriodStart { get; } // If ETag changes then this period must be subtracted from CachePeriodEnd.
	}
}