namespace SomeDataProvider.DataStorage.HistoryStores
{
	using Microsoft.Extensions.Logging;

	using SomeDataProvider.DataStorage.Definitions;

	public class SymbolHistoryStoreSingletonInstanceWithCaching : ISymbolHistoryStoreInstance
	{
		public SymbolHistoryStoreSingletonInstanceWithCaching(ICacheableSymbolHistoryStore store, ISymbolHistoryStoreCache cache, ILoggerFactory loggerFactory)
		{
			Store = new SymbolHistoryHybridStore(store, cache, loggerFactory);
		}

		public ISymbolHistoryStore Store { get; }

		public void Dispose()
		{
		}
	}
}