namespace SomeDataProvider.DataStorage.Definitions
{
	using System.Threading;
	using System.Threading.Tasks;

	public interface ICacheableSymbolHistoryStore : ISymbolHistoryStore
	{
		Task<ETag> GetActualETag(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default);
	}
}