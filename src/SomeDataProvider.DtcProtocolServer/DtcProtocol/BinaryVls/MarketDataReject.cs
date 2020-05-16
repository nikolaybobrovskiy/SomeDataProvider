// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

// ReSharper disable FieldCanBeMadeReadOnly.Global
namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	struct MarketDataReject
	{
		public ushort Size;
		public MessageTypeEnum Type;
		public ushort BaseSize;
		public uint SymbolId;
		public VariableLengthStringField RejectText;

		public MarketDataReject(uint symbolId)
			: this()
		{
			BaseSize = Size = Convert.ToUInt16(Marshal.SizeOf(this));
			Type = MessageTypeEnum.MarketDataReject;
			SymbolId = symbolId;
		}

		public void SetRejectText(string? val, byte[] stringsBuffer)
		{
			RejectText = RejectText.CreateStringValue(val, stringsBuffer, Size);
			Size += RejectText.Length;
		}
	}
}