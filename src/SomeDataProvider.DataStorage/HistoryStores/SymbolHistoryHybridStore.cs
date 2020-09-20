// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.HistoryStores
{
	using System;
	using System.Threading;
	using System.Threading.Tasks;

	using Microsoft.Extensions.Logging;

	using NBLib.Logging;

	using SomeDataProvider.DataStorage.Definitions;

	// Provides data from live and cache.
	public class SymbolHistoryHybridStore : ISymbolHistoryStore
	{
		static readonly ContinuationToken ContinueWithLiveData = (ContinuationToken)new Guid("017A85BD-984D-461F-BD9A-488ED4B31FBE");

		readonly ICacheableSymbolHistoryStore _store;
		readonly ISymbolHistoryStoreCache _cache;
		readonly ILogger<SymbolHistoryHybridStore> _logger;

		bool _isReadingFromCache;
		DateTime? _cachedPeriodEnd;
		ETag? _actualETag;
		ISymbolHistoryStoreCacheEntry? _cacheEntry;

		public SymbolHistoryHybridStore(ICacheableSymbolHistoryStore store, ISymbolHistoryStoreCache cache, ILoggerFactory loggerFactory)
		{
			_store = store;
			_cache = cache;
			_logger = loggerFactory.CreateLogger<SymbolHistoryHybridStore>();
		}

		public async Task<SymbolHistoryResponse> GetSymbolHistoryAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, ContinuationToken? continuationToken, CancellationToken cancellationToken = default)
		{
			return await _logger.LogOperationAsync(async () =>
			{
				if (continuationToken == null) // First request in a series.
				{
					_logger.LogDebug("FirstRequest");
					_actualETag = await _store.GetActualETag(symbol, historyInterval, cancellationToken);
					_logger.LogDebug("ActualETag: {actualETag}", _actualETag);
					_cacheEntry = await _cache.GetSymbolHistoryStoreCacheEntryAsync(symbol, historyInterval, cancellationToken);
					_logger.LogDebug("CacheEntryExists: {cacheEntryExists}", _cacheEntry != null);
					if (_cacheEntry != null)
					{
						_logger.LogDebug("Start: {start}", start);
						_logger.LogDebug("CachedPeriodStart: {cachedPeriodStart}", _cacheEntry.CachedPeriodStart);
						if (start >= _cacheEntry.CachedPeriodStart) // If earlier cached data is absent then read all from live.
						{
							_cachedPeriodEnd = _cacheEntry.CachedPeriodEnd;
							if (_cacheEntry.ETag.IsOutdated(_actualETag))
							{
								_cachedPeriodEnd = _cacheEntry.RevisablePeriodStart;
								_logger.LogDebug("CachedOutdatedSince: {revisablePeriodStart}", _cachedPeriodEnd);
							}
							_isReadingFromCache = true;
						}
					}
				}

				if (_isReadingFromCache)
				{
					_logger.LogDebug("ReadingFromCache");
					var cachedDataResponse = await _cache.GetSymbolHistoryAsync(symbol, historyInterval, start, end, limit, continuationToken, cancellationToken);
					_logger.LogDebug("RecordsRetrieved: {recordsFromCacheCount}", cachedDataResponse.Records.Count);
					var cachedDataResponseContinuationToken = cachedDataResponse.ContinuationToken;
					if (cachedDataResponseContinuationToken == default) // Cached data is over.
					{
						_logger.LogDebug("CacheDataIsOver");
						_isReadingFromCache = false;
						if (end <= _cachedPeriodEnd) // No live store reading needed, just stop getting data by returning default continuation token.
						{
							_logger.LogDebug("NoLiveDataReadingNeeded");
							return new SymbolHistoryResponse(cachedDataResponse.Records, default);
						}
						_logger.LogDebug("LiveDataReadingNeeded");
						// We cannot return empty continuation token, because it will stop getting data, but we need to continue from live store.
						return new SymbolHistoryResponse(cachedDataResponse.Records, ContinueWithLiveData);
					}
					return new SymbolHistoryResponse(cachedDataResponse.Records, cachedDataResponseContinuationToken);
				}

				if (continuationToken == ContinueWithLiveData)
				{
					continuationToken = null;
				}
				_logger.LogDebug("ReadingFromLive");
				var storeDataResponse = await _store.GetSymbolHistoryAsync(symbol, historyInterval, start, end, limit, continuationToken, cancellationToken);
				_logger.LogDebug("RecordsRetrieved: {liveRecordsCount}", storeDataResponse.Records.Count);
				if (storeDataResponse.Records.Count > 0)
				{
					_cacheEntry ??= await _cache.GetOrCreateSymbolHistoryStoreCacheEntryAsync(symbol, historyInterval, cancellationToken);
					await _cacheEntry.SaveRecordsAsync(storeDataResponse.Records, cancellationToken);
				}
				if (storeDataResponse.ContinuationToken == default)
				{
					_logger.LogDebug("LiveDataIsOver");
					// TODO: Update RevisablePeriodStart.
					if (_cacheEntry != null)
					{
						await _cacheEntry.UpdateEtagAsync(_actualETag!, cancellationToken);
					}
				}
				return storeDataResponse;
			}, "GetSymbolHistory({symbolCode},{historyInterval})", symbol.Code, historyInterval);
		}
	}
}