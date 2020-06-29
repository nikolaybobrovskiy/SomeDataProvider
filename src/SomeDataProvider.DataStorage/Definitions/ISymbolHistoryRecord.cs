// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Definitions
{
	using System;

	public interface ISymbolHistoryRecord
	{
		DateTime TimeStamp { get; }

		double OpenPrice { get; }

		double HighPrice { get; }

		double LowPrice { get; }

		double LastPrice { get; }

		double Volume { get; }
	}
}