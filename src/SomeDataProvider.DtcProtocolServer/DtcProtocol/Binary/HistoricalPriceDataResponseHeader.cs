// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

// ReSharper disable FieldCanBeMadeReadOnly.Global

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	struct HistoricalPriceDataResponseHeader
	{
		public ushort Size;
		public MessageTypeEnum Type;
		public int RequestId;
		public HistoricalDataIntervalEnum RecordInterval;
		public byte UseZLibCompression;
		public byte NoRecordsToReturn;
		public float IntToFloatPriceDivisor;

		public HistoricalPriceDataResponseHeader(int requestId, HistoricalDataIntervalEnum recordInterval, byte useZLibCompression, byte noRecordsToReturn, float intToFloatPriceDivisor)
			: this()
		{
			Size = Convert.ToUInt16(Marshal.SizeOf(this));
			Type = MessageTypeEnum.HistoricalPriceDataResponseHeader;
			RequestId = requestId;
			RecordInterval = recordInterval;
			UseZLibCompression = useZLibCompression;
			NoRecordsToReturn = noRecordsToReturn;
			IntToFloatPriceDivisor = intToFloatPriceDivisor;
		}
	}
}