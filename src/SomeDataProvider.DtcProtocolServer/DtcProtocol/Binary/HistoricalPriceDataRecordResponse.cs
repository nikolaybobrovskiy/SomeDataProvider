// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

// ReSharper disable FieldCanBeMadeReadOnly.Global

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	struct HistoricalPriceDataRecordResponse
	{
		public ushort Size;
		public MessageTypeEnum Type;
		public int RequestId;
		public long StartDateTime; // t_DateTime
		public double OpenPrice;
		public double HighPrice;
		public double LowPrice;
		public double LastPrice;
		public double Volume;
		public uint OpenInterestOrNumTrades;
		public double BidVolume;
		public double AskVolume;
		public byte IsFinalRecord;

		public HistoricalPriceDataRecordResponse(int requestId, long startDateTime, double lastPrice, byte isFinalRecord)
			: this()
		{
			Size = Convert.ToUInt16(Marshal.SizeOf(this));
			Type = MessageTypeEnum.HistoricalPriceDataRecordResponse;
			RequestId = requestId;
			StartDateTime = startDateTime;
			LastPrice = lastPrice;
			IsFinalRecord = isFinalRecord;
		}
	}
}