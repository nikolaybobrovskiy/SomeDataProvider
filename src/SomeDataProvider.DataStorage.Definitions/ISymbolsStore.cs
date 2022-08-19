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
	}
}