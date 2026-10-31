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

		float MinPriceIncrement { get; }

		string? Currency { get; }

		bool IsRealTime { get; }

		bool HasMarketDepth => false;

		double CurrencyValuePerIncrement => 0;

		double ContractSize => 0;

		DateTime? SecurityExpirationDate => null;

		string? ExchangeSymbol => null;

		bool IsDelayed { get; }

		bool IsDiscontinued { get; }

		DataService DataService { get; }

		string? DataServiceSettings { get; }
	}
}