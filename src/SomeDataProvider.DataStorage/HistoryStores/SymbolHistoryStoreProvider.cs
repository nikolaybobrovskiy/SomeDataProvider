// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.HistoryStores
{
	using System;
	using System.Collections.Concurrent;
	using System.Threading;
	using System.Threading.Tasks;

	using Microsoft.Extensions.Logging;
	using Microsoft.Extensions.Options;

	using NBLib.BuiltInTypes;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DataStorage.HistoryStores.Providers;

	public sealed class SymbolHistoryStoreProvider : ISymbolHistoryStoreProvider, IDisposable
	{
		readonly IOptions<SymbolHistoryTextFileStore.Options> _textFileStoreOpts;
		readonly ISymbolHistoryStoreCache _cache;
		readonly ILoggerFactory _loggerFactory;
		readonly object _singletonStoresLock = new();
		readonly ConcurrentDictionary<Fred.ServiceApiKey, SymbolHistoryFredStoreInstance> _fredStoreInstances = new();
		SymbolHistoryTextFileStore? _textFileStore;
		SymbolHistoryStatBureauStoreInstance? _statBureauStoreInstance;

		public SymbolHistoryStoreProvider(
			IOptions<SymbolHistoryTextFileStore.Options> textFileStoreOpts,
			ISymbolHistoryStoreCache cache,
			ILoggerFactory loggerFactory)
		{
			_textFileStoreOpts = textFileStoreOpts;
			_cache = cache;
			_loggerFactory = loggerFactory;
		}

		public Task<ISymbolHistoryStore> GetSymbolHistoryStoreAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken)
		{
			switch (symbol.DataService)
			{
				case DataService.TextFile:
					return GetTextFileStoreAsync(cancellationToken);
				case DataService.StatBureau:
					return GetStatBureauStoreAsync(cancellationToken);
				case DataService.Fred:
					return GetFredStoreAsync(cancellationToken);
				default:
					throw new NotImplementedException($"Store instance creation in not implemented: {symbol.DataService}.");
			}
		}

		public void Dispose()
		{
			_statBureauStoreInstance?.Dispose();
			foreach (var keyValuePair in _fredStoreInstances)
			{
				keyValuePair.Value.Dispose();
			}
		}

		Task<ISymbolHistoryStore> GetFredStoreAsync(CancellationToken cancellationToken)
		{
			// TODO: Need to take key from user context.
			var apiKey = (Fred.ServiceApiKey)"5e34dec427a5c32c3e45a70604b85459";
			var storeInstance = _fredStoreInstances.GetOrAddDisposable(apiKey, k => new SymbolHistoryFredStoreInstance(k, _cache, _loggerFactory));
			return Task.FromResult(storeInstance.Store);
		}

		Task<ISymbolHistoryStore> GetStatBureauStoreAsync(CancellationToken cancellationToken)
		{
			if (_statBureauStoreInstance != null)
				return Task.FromResult(_statBureauStoreInstance.Store);
			lock (_singletonStoresLock)
			{
				if (_statBureauStoreInstance == null)
				{
					_statBureauStoreInstance = new SymbolHistoryStatBureauStoreInstance(_cache, _loggerFactory);
				}
				return Task.FromResult(_statBureauStoreInstance.Store);
			}
		}

		Task<ISymbolHistoryStore> GetTextFileStoreAsync(CancellationToken cancellationToken)
		{
			if (_textFileStore != null)
				return Task.FromResult((ISymbolHistoryStore)_textFileStore);
			lock (_singletonStoresLock)
			{
				if (_textFileStore == null)
				{
					_textFileStore = new SymbolHistoryTextFileStore(_textFileStoreOpts, _loggerFactory);
				}
				return Task.FromResult((ISymbolHistoryStore)_textFileStore);
			}
		}

		class SymbolHistoryFredStoreInstance : IDisposable
		{
			SymbolHistoryFredStore _store;
			SymbolHistoryStoreCachingLayer _cachingLayer;

			public SymbolHistoryFredStoreInstance(Fred.ServiceApiKey apiKey, ISymbolHistoryStoreCache cache, ILoggerFactory loggerFactory)
			{
				_store = new SymbolHistoryFredStore(apiKey, loggerFactory);
				_cachingLayer = new SymbolHistoryStoreCachingLayer(_store, cache, loggerFactory);
			}

			//// public ISymbolHistoryStore Store => _cachingLayer;
			public ISymbolHistoryStore Store => _store;

			public void Dispose()
			{
				_store.Dispose();
			}
		}

		class SymbolHistoryStatBureauStoreInstance : IDisposable
		{
			SymbolHistoryStatBureauStore _store;
			SymbolHistoryStoreCachingLayer _cachingLayer;

			public SymbolHistoryStatBureauStoreInstance(ISymbolHistoryStoreCache cache, ILoggerFactory loggerFactory)
			{
				_store = new SymbolHistoryStatBureauStore(loggerFactory);
				_cachingLayer = new SymbolHistoryStoreCachingLayer(_store, cache, loggerFactory);
			}

			//// public ISymbolHistoryStore Store => _cachingLayer;
			public ISymbolHistoryStore Store => _store;

			public void Dispose()
			{
				_store.Dispose();
			}
		}
	}
}