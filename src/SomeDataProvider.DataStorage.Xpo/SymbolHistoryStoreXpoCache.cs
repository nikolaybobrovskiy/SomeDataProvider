// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

#pragma warning disable CC0022
namespace SomeDataProvider.DataStorage.Xpo
{
	using System;
	using System.Threading;
	using System.Threading.Tasks;

	using DevExpress.Xpo;
	using DevExpress.Xpo.DB.Exceptions;

	using Microsoft.Extensions.Logging;

	using NBLib.Exceptions;
	using NBLib.Logging;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DataStorage.Xpo.Entities;

	public class SymbolHistoryStoreXpoCache : ISymbolHistoryStoreCache
	{
		readonly IDataLayer _dataLayer;
		readonly ILogger<SymbolHistoryStoreXpoCache> _logger;

		public SymbolHistoryStoreXpoCache(IDataLayer dataLayer, ILoggerFactory loggerFactory)
		{
			_dataLayer = dataLayer;
			_logger = loggerFactory.CreateLogger<SymbolHistoryStoreXpoCache>();
		}

		public Task<SymbolHistoryResponse> GetSymbolHistoryAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, ContinuationToken? continuationToken, CancellationToken cancellationToken = default)
		{
			throw new NotImplementedException();
		}

		public async Task<ISymbolHistoryStoreCacheEntry?> GetSymbolHistoryStoreCacheEntryAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default)
		{
			return await _logger.LogOperationAsync(async () =>
			{
				using var session = new UnitOfWork(_dataLayer);
				return await session.Query<SymbolHistoryStoreCacheEntry>().FirstOrDefaultAsync(cancellationToken);
			}, "GetSymbolHistoryStoreCacheEntry({symbol},{historyInterval})", symbol.Code, historyInterval);
		}

		public async Task<ISymbolHistoryStoreCacheEntry> GetOrCreateSymbolHistoryStoreCacheEntryAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default)
		{
			return await _logger.LogOperationAsync(async () =>
			{
				using var session = new UnitOfWork(_dataLayer);
				var result = new SymbolHistoryStoreCacheEntry(session)
				{
					SymbolCode = symbol.Code,
					HistoryInterval = historyInterval,
				};
				try
				{
					await session.CommitChangesAsync(cancellationToken);
				}
				catch (Exception ex) when (ex.Is<ConstraintViolationException>())
				{
					_logger.LogDebug("SymbolHistoryStoreCacheEntryAlreadyExists");
					result = await session.Query<SymbolHistoryStoreCacheEntry>().FirstOrDefaultAsync(cancellationToken);
					if (result == null)
					{
						throw new InvalidOperationException("Could not create cache entry", ex);
					}
				}
				return result;
			}, "CreateSymbolHistoryStoreCacheEntry({symbol},{historyInterval})", symbol.Code, historyInterval);
		}

		public async Task<int> GetSymbolHistoryStoreCacheEntriesCountAsync(CancellationToken cancellationToken = default)
		{
			return await _logger.LogOperationAsync(async () =>
			{
				using var session = new UnitOfWork(_dataLayer);
				return await session.Query<SymbolHistoryStoreCacheEntry>().CountAsync(cancellationToken: cancellationToken);
			}, "GetSymbolHistoryStoreCacheEntriesCount()");
		}
	}
}