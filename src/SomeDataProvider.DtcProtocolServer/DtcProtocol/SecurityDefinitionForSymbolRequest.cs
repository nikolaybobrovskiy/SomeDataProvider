// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	class SecurityDefinitionForSymbolRequest
	{
		public SecurityDefinitionForSymbolRequest(int requestId, string symbol, string exchange)
		{
			RequestId = requestId;
			Symbol = symbol;
			Exchange = exchange;
		}

		public int RequestId { get; }

		public string Symbol { get; }

		public string Exchange { get; }

		public override string ToString()
		{
			return $"{RequestId}/{Symbol}/{Exchange}";
		}
	}
}