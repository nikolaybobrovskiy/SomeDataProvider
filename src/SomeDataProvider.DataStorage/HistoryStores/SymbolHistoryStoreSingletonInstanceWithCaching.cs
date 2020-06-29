namespace SomeDataProvider.DataStorage.HistoryStores
{
	using SomeDataProvider.DataStorage.Definitions;

	public class SymbolHistoryStoreSingletonInstanceWithCaching : ISymbolHistoryStoreInstance
	{
		public SymbolHistoryStoreSingletonInstanceWithCaching(ICacheableSymbolHistoryStore store, ISymbolHistoryStoreCache cache)
		{
			Store = new SymbolHistoryCacheStore(store, cache);
		}

		public ISymbolHistoryStore Store { get; }

		public void Dispose()
		{
		}
	}
}