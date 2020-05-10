namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
	struct LogonRequest
	{
		public ushort Size;

		public MessageTypeEnum Type;

		public int ProtocolVersion;

		public VariableLengthStringField Username;

		public VariableLengthStringField Password;

		public VariableLengthStringField GeneralTextData;

		public int Integer1;

		public int Integer2;

		public int HeartbeatIntervalInSeconds;

		public TradeModeEnum TradeMode;

		public VariableLengthStringField TradeAccount;

		public VariableLengthStringField HardwareIdentifier;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public VariableLengthStringField ClientName;

		public string GetUserName(byte[] buffer)
		{
		}
	}
}