namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	readonly struct LogonRequest
	{
		public readonly ushort Size;

		public readonly MessageTypeEnum Type;

		public readonly ushort BaseSize;

		public readonly int ProtocolVersion;

		public readonly VariableLengthStringField Username;

		public readonly VariableLengthStringField Password;

		public readonly VariableLengthStringField GeneralTextData;

		public readonly int Integer1;

		public readonly int Integer2;

		public readonly int HeartbeatIntervalInSeconds;

		public readonly TradeModeEnum TradeMode;

		public readonly VariableLengthStringField TradeAccount;

		public readonly VariableLengthStringField HardwareIdentifier;

		public readonly VariableLengthStringField ClientName;

		public string GetClientName(ReadOnlySpan<byte> buffer)
		{
			return ClientName.GetStringValue(buffer);
		}
	}
}