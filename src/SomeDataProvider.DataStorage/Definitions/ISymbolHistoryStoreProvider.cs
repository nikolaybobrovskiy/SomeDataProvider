namespace SomeDataProvider.DataStorage.Definitions
{
	using System.Threading;
	using System.Threading.Tasks;

	public interface ISymbolHistoryStoreProvider
	{
		public Task<ISymbolHistoryStore> GetSymbolHistoryStoreAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default);
	}
}