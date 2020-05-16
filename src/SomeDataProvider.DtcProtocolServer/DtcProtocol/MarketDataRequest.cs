// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class MarketDataRequest
	{
		public MarketDataRequest(RequestActionEnum requestAction, uint symbolId, string symbol, string exchange, uint intervalForSnapshotUpdatesInMilliseconds)
		{
			RequestAction = requestAction;
			SymbolId = symbolId;
			Symbol = symbol;
			Exchange = exchange;
			IntervalForSnapshotUpdatesInMilliseconds = intervalForSnapshotUpdatesInMilliseconds;
		}

		public RequestActionEnum RequestAction { get; }

		public uint SymbolId { get; }

		public string Symbol { get; }

		public string Exchange { get; }

		public uint IntervalForSnapshotUpdatesInMilliseconds { get; }

		public override string ToString()
		{
			return $"{RequestAction}/{SymbolId}/{Symbol}/{Exchange}/{IntervalForSnapshotUpdatesInMilliseconds}";
		}
	}
}