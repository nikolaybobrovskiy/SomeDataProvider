// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.HistoryStores
{
	using System;
	using System.Collections.Concurrent;

	using Microsoft.Extensions.Logging;
	using Microsoft.Extensions.Options;

	using NBLib.BuiltInTypes;

	using SomeDataProvider.DataStorage.Definitions;

	public class SymbolHistoryStoreInstanceFactory : ISymbolHistoryStoreInstanceFactory, IDisposable
	{
		readonly IOptions<SymbolHistoryTextFileStore.Options> _textFileStoreOpts;
		readonly ILoggerFactory _loggerFactory;
		readonly object _singletonStoresLock = new object();
		readonly ConcurrentDictionary<Fred.ServiceApiKey, SymbolHistoryFredStore> _fredStores = new ConcurrentDictionary<Fred.ServiceApiKey, SymbolHistoryFredStore>();
		SymbolHistoryTextFileStore? _textFileStore;
		SymbolHistoryStatBureauStore? _statBureauStore;

		public SymbolHistoryStoreInstanceFactory(
			IOptions<SymbolHistoryTextFileStore.Options> textFileStoreOpts,
			ILoggerFactory loggerFactory)
		{
			_textFileStoreOpts = textFileStoreOpts;
			_loggerFactory = loggerFactory;
		}

		public ISymbolHistoryStoreInstance CreateSymbolHistoryStoreInstance(ISymbol symbol, HistoryInterval historyInterval)
		{
			switch (symbol.DataService)
			{
				case DataService.TextFile:
					return GetTextFileStoreInstance();
				case DataService.StatBureau:
					return GetStatBureauStoreInstance();
				case DataService.Fred:
					return GetFredStoreInstance();
				default:
					throw new NotImplementedException($"Store instance creation in not implemented: {symbol.DataService}.");
			}
		}

		public void Dispose()
		{
			_statBureauStore?.Dispose();
			foreach (var keyValuePair in _fredStores)
			{
				keyValuePair.Value.Dispose();
			}
		}

		ISymbolHistoryStoreInstance GetFredStoreInstance()
		{
			// TODO: Need to take key from user context.
			var apiKey = (Fred.ServiceApiKey)"5e34dec427a5c32c3e45a70604b85459"!;
			var store = _fredStores.GetOrAddDisposable(apiKey, k => new SymbolHistoryFredStore(k));
			return new SymbolHistoryStoreSingletonInstance(store);
		}

		ISymbolHistoryStoreInstance GetStatBureauStoreInstance()
		{
			if (_statBureauStore != null)
				return new SymbolHistoryStoreSingletonInstance(_statBureauStore);
			lock (_singletonStoresLock)
			{
				if (_statBureauStore == null)
				{
					_statBureauStore = new SymbolHistoryStatBureauStore(_loggerFactory);
				}
				return new SymbolHistoryStoreSingletonInstance(_statBureauStore);
			}
		}

		ISymbolHistoryStoreInstance GetTextFileStoreInstance()
		{
			if (_textFileStore != null)
				return new SymbolHistoryStoreSingletonInstance(_textFileStore);
			lock (_singletonStoresLock)
			{
				if (_textFileStore == null)
				{
					_textFileStore = new SymbolHistoryTextFileStore(_textFileStoreOpts, _loggerFactory);
				}
				return new SymbolHistoryStoreSingletonInstance(_textFileStore);
			}
		}
	}
}