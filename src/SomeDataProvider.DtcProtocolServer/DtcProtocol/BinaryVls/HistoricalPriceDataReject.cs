// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

// ReSharper disable FieldCanBeMadeReadOnly.Global

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	struct HistoricalPriceDataReject
	{
		public ushort Size;
		public MessageTypeEnum Type;
		public ushort BaseSize;
		public int RequestId;
		public VariableLengthStringField RejectText;
		public HistoricalPriceDataRejectReasonCodeEnum RejectReasonCode;
		public ushort RetryTimeInSeconds;

		public HistoricalPriceDataReject(int requestId, HistoricalPriceDataRejectReasonCodeEnum rejectReasonCode)
			: this()
		{
			BaseSize = Size = Convert.ToUInt16(Marshal.SizeOf(this));
			Type = MessageTypeEnum.HistoricalPriceDataReject;
			RequestId = requestId;
			RejectReasonCode = rejectReasonCode;
		}

		public void SetRejectText(string? val, byte[] stringsBuffer)
		{
			RejectText = RejectText.CreateStringValue(val, stringsBuffer, Size);
			Size += RejectText.Length;
		}
	}
}