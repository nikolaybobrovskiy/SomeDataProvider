// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

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

		bool IsDiscontinued { get; }

		DataService DataService { get; set; }

		string? DataServiceSettings { get; set; }
	}
}