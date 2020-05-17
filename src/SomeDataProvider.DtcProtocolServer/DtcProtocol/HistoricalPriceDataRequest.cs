// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using System;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class HistoricalPriceDataRequest
	{
		public HistoricalPriceDataRequest(int requestId, string symbol, string exchange, HistoricalDataIntervalEnum recordInterval, DateTime startDateTime, DateTime endDateTime, uint maxDaysToReturn, bool useZLibCompression)
		{
			RequestId = requestId;
			Symbol = symbol;
			Exchange = exchange;
			UseZLibCompression = useZLibCompression;
			StartDateTime = startDateTime;
			EndDateTime = endDateTime;
			MaxDaysToReturn = maxDaysToReturn;
			RecordInterval = recordInterval;
		}

		public int RequestId { get; }

		public string Symbol { get; }

		public string Exchange { get; }

		public HistoricalDataIntervalEnum RecordInterval { get; }

		public DateTime StartDateTime { get; }

		public DateTime EndDateTime { get; }

		public uint MaxDaysToReturn { get; }

		public bool UseZLibCompression { get; }

		public override string ToString()
		{
			return $"{RequestId}/{Symbol}/{Exchange}/{RecordInterval}/{StartDateTime:o}/{EndDateTime:o}/{MaxDaysToReturn}/{UseZLibCompression}";
		}
	}
}