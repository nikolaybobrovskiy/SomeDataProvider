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
	public class SymbolHistoryStoreCachingLayer : ISymbolHistoryStore
	{
		readonly ICacheableSymbolHistoryStore _store;
		readonly ISymbolHistoryStoreCache _cache;
		readonly ILoggerFactory _loggerFactory;

		public SymbolHistoryStoreCachingLayer(ICacheableSymbolHistoryStore store, ISymbolHistoryStoreCache cache, ILoggerFactory loggerFactory)
		{
			_store = store;
			_cache = cache;
			_loggerFactory = loggerFactory;
		}

		public Task<ISymbolHistoryStoreReader> CreateSymbolHistoryReaderAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, CancellationToken cancellationToken = default)
		{
#pragma warning disable CC0022 // Should dispose object
			return Task.FromResult((ISymbolHistoryStoreReader)new Reader(_store, _cache, symbol, historyInterval, start, end, limit, _loggerFactory));
#pragma warning restore CC0022
		}

		class Reader : SymbolHistoryStoreReaderBase<Reader>
		{
			readonly ICacheableSymbolHistoryStore _store;
			readonly ISymbolHistoryStoreCache _cache;

			ISymbolHistoryStoreReader? _liveStoreReader;
			ISymbolHistoryStoreReader? _cacheReader;
			bool _isReadingFromCache;
			DateTime? _cachedPeriodEnd;
			ETag? _actualETag;
			ISymbolHistoryStoreCacheEntry? _cacheEntry;

			public Reader(ICacheableSymbolHistoryStore store, ISymbolHistoryStoreCache cache, ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, ILoggerFactory loggerFactory)
				: base(symbol, historyInterval, start, end, limit, loggerFactory)
			{
				_store = store;
				_cache = cache;
			}

			public override async Task<SymbolHistoryResponse> ReadSymbolHistoryAsync(CancellationToken cancellationToken = default)
			{
				return await L.LogOperationAsync(async () =>
				{
					if (_actualETag == null) // First request by reader.
					{
						L.LogDebug("FirstRequest");
						_actualETag = await _store.GetActualETag(Symbol, HistoryInterval, cancellationToken);
						L.LogDebug("ActualETag: {actualETag}", _actualETag);
						_cacheEntry = await _cache.GetSymbolHistoryStoreCacheEntryAsync(Symbol, HistoryInterval, cancellationToken);
						L.LogDebug("CacheEntryExists: {cacheEntryExists}", _cacheEntry != null);
						if (_cacheEntry != null)
						{
							L.LogDebug("Start: {start}", Start);
							L.LogDebug("CachedPeriodStart: {cachedPeriodStart}", _cacheEntry.CachedPeriodStart);
							if (Start >= _cacheEntry.CachedPeriodStart)
							{
								_cachedPeriodEnd = _cacheEntry.CachedPeriodEnd;
								if (_cacheEntry.ETag.IsOutdated(_actualETag))
								{
									_cachedPeriodEnd = _cacheEntry.RevisablePeriodStart;
									// TODO: Get new RevisablePeriodStart
									L.LogDebug("CachedOutdatedSince: {revisablePeriodStart}", _cachedPeriodEnd);
								}
								_isReadingFromCache = true;
							}
							else
							{
								// If earlier cached data is absent then read all from live.
								L.LogDebug("EarlierThenCachedDataPeriodRequest: ReadFromLive");
							}
						}
					}

					if (_isReadingFromCache)
					{
						L.LogDebug("ReadingFromCache");
						_cacheReader ??= await _cache.CreateSymbolHistoryReaderAsync(Symbol, HistoryInterval, Start, End, Limit, cancellationToken: cancellationToken);
						var cachedDataResponse = await _cacheReader.ReadSymbolHistoryAsync(cancellationToken);
						L.LogDebug("RecordsRetrieved: {recordsFromCacheCount}", cachedDataResponse.Records.Count);
						var cachedDataIsOver = !cachedDataResponse.HasMore;
						if (cachedDataIsOver) // Cached data is over.
						{
							L.LogDebug("CacheDataIsOver");
							_isReadingFromCache = false;
							if (End <= _cachedPeriodEnd) // No live store reading needed, just stop getting data by returning default continuation token.
							{
								L.LogDebug("NoLiveDataReadingNeeded");
								return new SymbolHistoryResponse(cachedDataResponse.Records, false);
							}
							L.LogDebug("LiveDataReadingNeeded");
							return new SymbolHistoryResponse(cachedDataResponse.Records, true);
						}
						return new SymbolHistoryResponse(cachedDataResponse.Records, true);
					}

					L.LogDebug("ReadingFromLive");
					// TODO: Start should be after last end from cached data.
					_liveStoreReader ??= await _store.CreateSymbolHistoryReaderAsync(Symbol, HistoryInterval, Start, End, Limit, cancellationToken: cancellationToken);
					var storeDataResponse = await _liveStoreReader.ReadSymbolHistoryAsync(cancellationToken);
					L.LogDebug("RecordsRetrieved: {liveRecordsCount}", storeDataResponse.Records.Count);
					if (storeDataResponse.Records.Count > 0)
					{
						_cacheEntry ??= await _cache.GetOrCreateSymbolHistoryStoreCacheEntryAsync(Symbol, HistoryInterval, cancellationToken);
						await _cacheEntry.SaveRecordsAsync(storeDataResponse.Records, cancellationToken);
					}
					if (!storeDataResponse.HasMore)
					{
						L.LogDebug("LiveDataIsOver");
						// TODO: Update RevisablePeriodStart.
						if (_cacheEntry != null)
						{
							await _cacheEntry.UpdateEtagAsync(_actualETag!, cancellationToken);
						}
					}
					return storeDataResponse;
				}, "ReadSymbolHistoryAsync({symbolCode},{historyInterval})", Symbol.Code, HistoryInterval);
			}
		}
	}
}