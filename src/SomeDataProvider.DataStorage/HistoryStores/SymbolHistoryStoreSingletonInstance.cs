// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.HistoryStores
{
	using SomeDataProvider.DataStorage.Definitions;

	public class SymbolHistoryStoreSingletonInstance : ISymbolHistoryStoreInstance
	{
		public SymbolHistoryStoreSingletonInstance(ISymbolHistoryStore store)
		{
			Store = store;
		}

		public ISymbolHistoryStore Store { get; }

		public void Dispose()
		{
		}
	}
}