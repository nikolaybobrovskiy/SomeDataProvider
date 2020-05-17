namespace SomeDataProvider.DataStorage.Definitions
{
	public interface ISymbol
	{
		string Code { get; }

		string? Exchange { get; }

		SymbolType Type { get; }

		string? Description { get; }

		string? Category { get; }

		int NumberOfDecimals { get; }

		string? Currency { get; }

		bool IsRealTime { get; }

		bool IsDelayed { get; }

		DataService DataService { get; set; }
	}
}