namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	struct LogonRequest
	{
		public ushort Size;

		public MessageTypeEnum Type;

		public int ProtocolVersion;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = TextStringLengths.UsernamePasswordLength)]
		public string Username;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = TextStringLengths.UsernamePasswordLength)]
		public string Password;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = TextStringLengths.GeneralIdentifierLength)]
		public string GeneralTextData;

		public int Integer1;

		public int Integer2;

		public int HeartbeatIntervalInSeconds;

		public TradeModeEnum TradeMode;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = TextStringLengths.TradeAccountLength)]
		public string TradeAccount;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = TextStringLengths.GeneralIdentifierLength)]
		public string HardwareIdentifier;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string ClientName;
	}
}