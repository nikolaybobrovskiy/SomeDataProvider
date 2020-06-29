// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.HistoryStores
{
	using System;
	using System.Threading;
	using System.Threading.Tasks;

	using NBLib.BuiltInTypes;
	using NBLib.DateTime;

	using SomeDataProvider.DataStorage.Definitions;

	// TODO: This is created per request. So we can use class props to keep current request serving state instead of complex continuation token.
	public class SymbolHistoryCacheStore : ISymbolHistoryStore
	{
		readonly ICacheableSymbolHistoryStore _store;
		readonly ISymbolHistoryStoreCache _cache;

		public SymbolHistoryCacheStore(ICacheableSymbolHistoryStore store, ISymbolHistoryStoreCache cache)
		{
			_store = store;
			_cache = cache;
		}

		public async Task<SymbolHistoryResponse> GetSymbolHistoryAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, ContinuationToken? continuationToken, CancellationToken cancellationToken = default)
		{
			// We should fix all initial params in continuation token:
			// 0. Type of current data source: cache or store.
			// 1. Actual value of continuation token.
			// 2. ETag.
			// 3. CachedPeriodStart.
			// 4. CachedPeriodEnd.
			DateTime? cachedPeriodStart = null;
			DateTime? cachedPeriodEnd = null;
			bool isReadingFromCache = false;
			ContinuationToken? actualContinuationToken = null;
			ETag actualETag;

			if (continuationToken != null)
			{
				var continuationTokenParts = continuationToken.Value.Split('|');
				isReadingFromCache = continuationTokenParts[0] == "1";
				actualContinuationToken = continuationTokenParts[1].IsEmpty() ? null : (ContinuationToken)continuationTokenParts[1];
				actualETag = continuationTokenParts[2].IsEmpty() ? ETag.Empty : (ETag)continuationTokenParts[2];
				if (!continuationTokenParts[3].IsEmpty())
					cachedPeriodStart = continuationTokenParts[3].FromDateTimeIsoString();
				if (!continuationTokenParts[4].IsEmpty())
					cachedPeriodEnd = continuationTokenParts[4].FromDateTimeIsoString();
			}
			else
			{
				actualETag = await _store.GetActualETag(symbol, historyInterval, cancellationToken);
				var cacheEntry = await _cache.GetSymbolHistoryStoreCacheEntry(symbol, cancellationToken).ConfigureAwait(false);
				if (cacheEntry != null)
				{
					cachedPeriodStart = cacheEntry.CachedPeriodStart;
					cachedPeriodEnd = cacheEntry.CachedPeriodEnd;
					if (cacheEntry.ETag.IsOutdated(actualETag))
					{
						cachedPeriodEnd = cacheEntry.RevisablePeriodStart;
					}
					isReadingFromCache = true;
				}
			}

			if (isReadingFromCache)
			{
				var cachedDataResponse = await _cache.GetSymbolHistoryAsync(symbol, historyInterval, cachedPeriodStart!.Value, cachedPeriodEnd!.Value, limit, actualContinuationToken, cancellationToken);
				var cachedDataResponseContinuationToken = cachedDataResponse.ContinuationToken;
				var nextReadFromCache = "1";
				if (cachedDataResponseContinuationToken == default) // Cached data is over.
				{
					nextReadFromCache = "0";
					if (end <= cachedPeriodEnd)
					{
						return new SymbolHistoryResponse(cachedDataResponse.Records, default);
					}
				}
				var newContinuationToken = $"{nextReadFromCache}|{cachedDataResponseContinuationToken}|{actualETag}|{cachedPeriodStart?.ToIsoString()}|{cachedPeriodEnd?.ToIsoString()}";
				return new SymbolHistoryResponse(cachedDataResponse.Records, (ContinuationToken)newContinuationToken);
			}

			// TODO: Put data to cache and create/update cache entry.
			var storeDataResponse = await _store.GetSymbolHistoryAsync(symbol, historyInterval, start, end, limit, actualContinuationToken, cancellationToken).ConfigureAwait(false);
			var storeDataResponseContinuationToken = storeDataResponse.ContinuationToken;
			if (storeDataResponseContinuationToken == default)
			{
				return storeDataResponse;
			}
			var newLiveDataContinuationToken = $"0|{storeDataResponseContinuationToken}|{actualETag}|{cachedPeriodStart?.ToIsoString()}|{cachedPeriodEnd?.ToIsoString()}";
			return new SymbolHistoryResponse(storeDataResponse.Records, (ContinuationToken)newLiveDataContinuationToken);
		}
	}
}