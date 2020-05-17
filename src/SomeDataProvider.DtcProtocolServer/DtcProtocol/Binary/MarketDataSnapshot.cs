// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

// ReSharper disable FieldCanBeMadeReadOnly.Global

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	struct MarketDataSnapshot
	{
		public ushort Size;
		public MessageTypeEnum Type;
		public uint SymbolId;
		public double SessionSettlementPrice;
		public double SessionOpenPrice;
		public double SessionHighPrice;
		public double SessionLowPrice;
		public double SessionVolume;
		public uint SessionNumTrades;
		public uint OpenInterest;
		public double BidPrice;
		public double AskPrice;
		public double AskQuantity;
		public double BidQuantity;
		public double LastTradePrice;
		public double LastTradeVolume;
		public double LastTradeDateTime; // t_DateTimeWithMilliseconds
		public double BidAskDateTime; // t_DateTimeWithMilliseconds
		public uint SessionSettlementDateTime; // t_DateTime4Byte
		public uint TradingSessionDate; // t_DateTime4Byte
		public TradingStatusEnum TradingStatus;
		public double MarketDepthUpdateDateTime; // t_DateTimeWithMilliseconds

		public MarketDataSnapshot(uint symbolId, TradingStatusEnum tradingStatus)
			: this()
		{
			Size = Convert.ToUInt16(Marshal.SizeOf(this));
			Type = MessageTypeEnum.MarketDataSnapshot;
			SymbolId = symbolId;
			TradingStatus = tradingStatus;

			SessionSettlementPrice = double.MaxValue;
			SessionOpenPrice = double.MaxValue;
			SessionHighPrice = double.MaxValue;
			SessionLowPrice = double.MaxValue;
			SessionVolume = double.MaxValue;
			SessionNumTrades = uint.MaxValue;

			OpenInterest = uint.MaxValue;

			BidPrice = double.MaxValue;
			AskPrice = double.MaxValue;
			AskQuantity = double.MaxValue;
			BidQuantity = double.MaxValue;

			LastTradePrice = double.MaxValue;
			LastTradeVolume = double.MaxValue;
		}
	}
}