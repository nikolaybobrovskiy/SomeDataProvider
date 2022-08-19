namespace SomeDataProvider.DataStorage.Definitions
{
	using System.Threading;
	using System.Threading.Tasks;

	public interface ISymbolHistoryStoreProvider
	{
		public ValueTask<ISymbolHistoryStore> GetSymbolHistoryStoreAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default);
	}
}