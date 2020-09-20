namespace SomeDataProvider.DataStorage.Definitions
{
	using System;
	using System.Collections.Generic;
	using System.Threading;
	using System.Threading.Tasks;

	public interface ISymbolHistoryStoreCache : ISymbolHistoryStore
	{
		Task<ISymbolHistoryStoreCacheEntry?> GetSymbolHistoryStoreCacheEntryAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default);

		Task<ISymbolHistoryStoreCacheEntry> GetOrCreateSymbolHistoryStoreCacheEntryAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default);

		Task<int> GetSymbolHistoryStoreCacheEntriesCountAsync(CancellationToken cancellationToken = default);
	}

	public interface ISymbolHistoryStoreCacheEntry
	{
		DateTime CachedPeriodStart { get; } // As requested.

		DateTime CachedPeriodEnd { get; } // As requested, can be infinity.

		ETag ETag { get; } // Like last update/release date. Cannot be null. Otherwise cache has no sense.

		DateTime RevisablePeriodStart { get; } // If ETag changes then this period must be subtracted from CachePeriodEnd.

		Task UpdateEtagAsync(ETag eTag, CancellationToken cancellationToken = default);

		Task SaveRecordsAsync(IReadOnlyCollection<ISymbolHistoryRecord> records, CancellationToken cancellationToken = default);
	}
}