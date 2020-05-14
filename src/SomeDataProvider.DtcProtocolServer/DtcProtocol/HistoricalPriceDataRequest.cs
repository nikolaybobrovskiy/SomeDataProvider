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