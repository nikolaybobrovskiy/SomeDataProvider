namespace SomeDataProvider.DataStorage.Definitions;

public interface ISymbolsStoreProvider
{
	string SourcesDescription => "FRED";

	ValueTask<(ISymbolsStore SymbolsStore, string SymbolCode)?> GetSymbolsStoreAsync(string code, CancellationToken cancellationToken = default);

	ValueTask<(ISymbolsStore SymbolsStore, string SymbolCode)?> GetSymbolsStoreAsync(string code, string exchange, CancellationToken cancellationToken = default) =>
		string.IsNullOrEmpty(exchange) ? GetSymbolsStoreAsync(code, cancellationToken) : ValueTask.FromResult<(ISymbolsStore, string)?>(null);

	ValueTask<IReadOnlyCollection<ISymbol>> GetKnownSymbolsAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Finds symbols matching <paramref name="searchText"/> across the provider's stores, with at most
	/// <paramref name="limit"/> results, in their own order.
	/// </summary>
	ValueTask<IReadOnlyCollection<ISymbol>> SearchSymbolsAsync(string searchText, SymbolSearchType searchType, int limit, CancellationToken cancellationToken = default);

	ValueTask<IReadOnlyCollection<ISymbol>> SearchSymbolsAsync(string searchText, SymbolSearchType searchType, int limit, string exchange, SymbolType securityType, CancellationToken cancellationToken = default) =>
		string.IsNullOrEmpty(exchange) && securityType == SymbolType.Unset
			? SearchSymbolsAsync(searchText, searchType, limit, cancellationToken)
			: ValueTask.FromResult<IReadOnlyCollection<ISymbol>>(Array.Empty<ISymbol>());
}