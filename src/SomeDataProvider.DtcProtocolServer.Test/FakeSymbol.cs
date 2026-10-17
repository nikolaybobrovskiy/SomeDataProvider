// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using SomeDataProvider.DataStorage.Definitions;

	sealed class FakeSymbol : ISymbol
	{
		public FakeSymbol(string code)
		{
			Code = code;
		}

		public string Code { get; }

		public string? Exchange => null;

		public SymbolType Type => SymbolType.Unset;

		public string? Description => "Fake series (Index/D/NSA)";

		public string? Category => null;

		public int NumberOfDecimals => 2;

		public float MinPriceIncrement => 0.01F;

		public string? Currency => null;

		public bool IsRealTime => false;

		public bool IsDelayed => true;

		public bool IsDiscontinued => false;

		public DataService DataService => DataService.Fred;

		public string? DataServiceSettings => null;
	}
}
