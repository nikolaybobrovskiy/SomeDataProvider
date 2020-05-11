// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

// ReSharper disable FieldCanBeMadeReadOnly.Global

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	struct LogonResponse
	{
		public ushort Size;
		public MessageTypeEnum Type;
		public ushort BaseSize;
		public int ProtocolVersion;
		public LogonStatusEnum Result;
		public VariableLengthStringField ResultText;
		public VariableLengthStringField ReconnectAddress;
		public int Integer1;
		public VariableLengthStringField ServerName;
		public byte MarketDepthUpdatesBestBidAndAsk;
		public byte TradingIsSupported;
		public byte OCOOrdersSupported;
		public byte OrderCancelReplaceSupported;
		public VariableLengthStringField SymbolExchangeDelimiter;
		public byte SecurityDefinitionsSupported;
		public byte HistoricalPriceDataSupported;
		public byte ResubscribeWhenMarketDataFeedAvailable;
		public byte MarketDepthIsSupported;
		public byte OneHistoricalPriceDataRequestPerConnection;
		public byte BracketOrdersSupported;
		public byte UseIntegerPriceOrderMessages;
		public byte UsesMultiplePositionsPerSymbolAndTradeAccount;
		public byte MarketDataSupported;

		public LogonResponse(LogonStatusEnum result)
			: this()
		{
			BaseSize = Size = Convert.ToUInt16(Marshal.SizeOf(typeof(LogonResponse)));
			ProtocolVersion = MessageProtocol.Version;
			Type = MessageTypeEnum.LogonResponse;
			Result = result;
		}

		public void SetResultText(string? val, byte[] stringsBuffer)
		{
			ResultText = ResultText.CreateStringValue(val, stringsBuffer, Size);
			Size += ResultText.Length;
		}
	}
}