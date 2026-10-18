// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Definitions
{
	using System.Collections.Generic;
	using System.Threading;
	using System.Threading.Tasks;

	public interface ISymbolsStore
	{
		ValueTask<ISymbol?> GetSymbolAsync(string code, CancellationToken cancellationToken = default);

		ValueTask<IReadOnlyCollection<ISymbol>> GetKnownSymbolsAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// Finds symbols matching <paramref name="searchText"/>, in the store's own order, with at most
		/// <paramref name="limit"/> results.
		/// </summary>
		ValueTask<IReadOnlyCollection<ISymbol>> SearchSymbolsAsync(string searchText, SymbolSearchType searchType, int limit, CancellationToken cancellationToken = default);
	}
}