// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.HistoryStores
{
	using System;

	using Microsoft.Extensions.Logging;
	using Microsoft.Extensions.Options;

	using SomeDataProvider.DataStorage.Definitions;

	public class SymbolHistoryStoreInstanceFactory : ISymbolHistoryStoreInstanceFactory
	{
		readonly IOptions<SymbolHistoryTextFileStore.Options> _textFileStoreOpts;
		readonly ILoggerFactory _loggerFactory;
		readonly object _singletonStoresLock = new object();
		SymbolHistoryTextFileStore? _textFileStore;

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
				default:
					throw new NotImplementedException($"Store instance creation in not implemented: {symbol.DataService}.");
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