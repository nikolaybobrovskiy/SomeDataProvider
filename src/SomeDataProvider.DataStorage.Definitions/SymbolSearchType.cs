// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Definitions
{
	/// <summary>
	/// How a symbol search's SearchText should be matched. Decoupled from the DTC wire's SearchTypeEnum, the same way
	/// <see cref="SymbolType"/> is decoupled from the wire's SecurityTypeEnum.
	/// </summary>
	public enum SymbolSearchType
	{
		/// <summary>Match SearchText against a symbol code (FRED: search_type=series_id).</summary>
		BySymbol = 0,

		/// <summary>Match SearchText against the symbol's description, full-text (FRED: search_type=full_text).</summary>
		ByDescription = 1,
	}
}
