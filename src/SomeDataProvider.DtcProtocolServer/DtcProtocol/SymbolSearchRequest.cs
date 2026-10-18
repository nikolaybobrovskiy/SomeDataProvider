// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using Dtc.Protocol;

	class SymbolSearchRequest
	{
		public SymbolSearchRequest(int requestId, string searchText, string exchange, SecurityTypeEnum securityType, SearchTypeEnum searchType)
		{
			RequestId = requestId;
			SearchText = searchText;
			Exchange = exchange;
			SecurityType = securityType;
			SearchType = searchType;
		}

		public int RequestId { get; }

		public string SearchText { get; }

		public string Exchange { get; }

		public SecurityTypeEnum SecurityType { get; }

		public SearchTypeEnum SearchType { get; }

		public override string ToString()
		{
			return $"{RequestId}/{SearchText}/{Exchange}/{SecurityType}/{SearchType}";
		}
	}
}
