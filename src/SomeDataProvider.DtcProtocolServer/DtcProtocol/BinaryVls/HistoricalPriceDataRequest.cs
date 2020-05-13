namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	readonly struct HistoricalPriceDataRequest
	{
		public readonly ushort Size;
		public readonly MessageTypeEnum Type;
		public readonly ushort BaseSize;
		public readonly int RequestId;
		public readonly VariableLengthStringField Symbol;
		public readonly VariableLengthStringField Exchange;
		public readonly HistoricalDataIntervalEnum RecordInterval;
		public readonly long StartDateTime;
		public readonly long EndDateTime;
		public readonly uint MaxDaysToReturn;
		public readonly byte UseZLibCompression;
		public readonly byte RequestDividendAdjustedStockData;
		public readonly byte Flag1;

		public string GetSymbol(ReadOnlySpan<byte> buffer)
		{
			return Symbol.GetStringValue(buffer);
		}

		public string GetExchange(ReadOnlySpan<byte> buffer)
		{
			return Exchange.GetStringValue(buffer);
		}
	}
}