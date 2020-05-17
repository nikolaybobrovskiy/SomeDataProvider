// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class HistoricalPriceDataRequest
	{
		public HistoricalPriceDataRequest(int requestId, string symbol, string exchange, HistoricalDataIntervalEnum recordInterval, bool useZLibCompression)
		{
			RequestId = requestId;
			Symbol = symbol;
			Exchange = exchange;
			UseZLibCompression = useZLibCompression;
			RecordInterval = recordInterval;
		}

		public int RequestId { get; }

		public string Symbol { get; }

		public string Exchange { get; }

		public HistoricalDataIntervalEnum RecordInterval { get; }

		public bool UseZLibCompression { get; }
	}
}