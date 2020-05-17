// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Definitions
{
	public interface ISymbolHistoryStoreInstanceFactory
	{
		ISymbolHistoryStoreInstance CreateSymbolHistoryStoreInstance(ISymbol symbol, HistoryInterval historyInterval);
	}
}