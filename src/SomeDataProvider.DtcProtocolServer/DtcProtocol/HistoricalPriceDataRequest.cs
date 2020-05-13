namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	class HistoricalPriceDataRequest
	{
		public HistoricalPriceDataRequest(int requestId, string symbol, string exchange, bool useZLibCompression)
		{
			RequestId = requestId;
			Symbol = symbol;
			Exchange = exchange;
			UseZLibCompression = useZLibCompression;
		}

		public int RequestId { get; }

		public string Symbol { get; }

		public string Exchange { get; }

		public bool UseZLibCompression { get; }
	}
}