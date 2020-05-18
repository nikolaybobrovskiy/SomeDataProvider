// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.HistoryStores
{
	using System;

	using SomeDataProvider.DataStorage.Definitions;

	public class SymbolHistoryStoreTransientInstance : ISymbolHistoryStoreInstance
	{
		IDisposable _store;

		public SymbolHistoryStoreTransientInstance(IDisposable store)
		{
			// ReSharper disable once SuspiciousTypeConversion.Global
			if (!(store is ISymbolHistoryStore))
			{
				throw new ArgumentException("store must be a ISymbolHistoryStore instance", nameof(store));
			}
			_store = store;
		}

		// ReSharper disable once SuspiciousTypeConversion.Global
		public ISymbolHistoryStore Store => (ISymbolHistoryStore)_store;

		public void Dispose()
		{
			_store.Dispose();
		}
	}
}