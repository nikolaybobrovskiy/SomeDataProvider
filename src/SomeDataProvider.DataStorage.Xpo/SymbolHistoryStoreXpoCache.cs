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

	// We'll return XPO entity instance which will be invalidated, because session is disposed at the end of method. Should not be a problem.
	public class SymbolHistoryStoreXpoCache : ISymbolHistoryStoreCache
	{
		readonly ILoggerFactory _loggerFactory;
		readonly ILogger<SymbolHistoryStoreXpoCache> _logger;

		public SymbolHistoryStoreXpoCache(IDataLayer dataLayer, ILoggerFactory loggerFactory)
		{
			_loggerFactory = loggerFactory;
			DataLayer = dataLayer;
			_logger = loggerFactory.CreateLogger<SymbolHistoryStoreXpoCache>();
		}

		internal IDataLayer DataLayer { get; }

		public async Task<ISymbolHistoryStoreCacheEntry?> GetSymbolHistoryStoreCacheEntryAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default)
		{
			return await _logger.LogOperationAsync(async () =>
			{
				using var session = new UnitOfWork(DataLayer);
				var result = await session.Query<SymbolHistoryStoreCacheEntry>().FirstOrDefaultAsync(cancellationToken);
				if (result != null)
				{
					result.Store = this;
				}
				return result;
			}, "GetSymbolHistoryStoreCacheEntry({symbol},{historyInterval})", symbol.Code, historyInterval);
		}

		public async Task<ISymbolHistoryStoreCacheEntry> GetOrCreateSymbolHistoryStoreCacheEntryAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default)
		{
			return await _logger.LogOperationAsync(async () =>
			{
				using var session = new UnitOfWork(DataLayer);
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
				result.Store = this;
				return result;
			}, "CreateSymbolHistoryStoreCacheEntry({symbol},{historyInterval})", symbol.Code, historyInterval);
		}

		public async Task<int> GetSymbolHistoryStoreCacheEntriesCountAsync(CancellationToken cancellationToken = default)
		{
			return await _logger.LogOperationAsync(async () =>
			{
				using var session = new UnitOfWork(DataLayer);
				return await session.Query<SymbolHistoryStoreCacheEntry>().CountAsync(cancellationToken: cancellationToken);
			}, "GetSymbolHistoryStoreCacheEntriesCount()");
		}

		public Task<ISymbolHistoryStoreReader> CreateSymbolHistoryReaderAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, CancellationToken cancellationToken = default)
		{
			return Task.FromResult((ISymbolHistoryStoreReader)new Reader(symbol, historyInterval, start, end, limit, _loggerFactory));
		}

		class Reader : SymbolHistoryStoreReaderBase<Reader>
		{
			public Reader(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, ILoggerFactory loggerFactory)
				: base(symbol, historyInterval, start, end, limit, loggerFactory)
			{
			}

			public override Task<SymbolHistoryResponse> ReadSymbolHistoryAsync(CancellationToken cancellationToken = default)
			{
				throw new NotImplementedException();
			}
		}
	}
}