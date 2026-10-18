namespace SomeDataProvider.DataStorage.Definitions;

public interface ISymbolsStoreProvider
{
	ValueTask<(ISymbolsStore SymbolsStore, string SymbolCode)?> GetSymbolsStoreAsync(string code, CancellationToken cancellationToken = default);

	ValueTask<IReadOnlyCollection<ISymbol>> GetKnownSymbolsAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Finds symbols matching <paramref name="searchText"/> across the provider's stores, with at most
	/// <paramref name="limit"/> results, in their own order.
	/// </summary>
	ValueTask<IReadOnlyCollection<ISymbol>> SearchSymbolsAsync(string searchText, SymbolSearchType searchType, int limit, CancellationToken cancellationToken = default);
}