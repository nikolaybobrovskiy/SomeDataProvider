namespace SomeDataProvider.DataStorage.Definitions
{
	using System.Threading;
	using System.Threading.Tasks;

	public interface ISymbolsStore
	{
		Task<ISymbol?> GetSymbolAsync(string code, CancellationToken cancellationToken = default);
	}
}