// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Definitions
{
	using System;

	public sealed class SymbolHistoryRecord
	{
		public SymbolHistoryRecord(DateTime timeStamp, double openPrice, double highPrice, double lowPrice, double lastPrice, double volume)
		{
			TimeStamp = timeStamp;
			OpenPrice = openPrice;
			HighPrice = highPrice;
			LowPrice = lowPrice;
			LastPrice = lastPrice;
			Volume = volume;
		}

		public DateTime TimeStamp { get; }

		public double OpenPrice { get; }

		public double HighPrice { get; }

		public double LowPrice { get; }

		public double LastPrice { get; }

		public double Volume { get; }
	}
}