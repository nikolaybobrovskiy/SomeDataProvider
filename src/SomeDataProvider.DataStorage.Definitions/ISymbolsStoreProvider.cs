namespace SomeDataProvider.DataStorage.Definitions;

public interface ISymbolsStoreProvider
{
	ValueTask<(ISymbolsStore SymbolsStore, string SymbolCode)?> GetSymbolsStoreAsync(string code, CancellationToken cancellationToken = default);
	
	ValueTask<IReadOnlyCollection<ISymbol>> GetKnownSymbolsAsync(CancellationToken cancellationToken = default);
}