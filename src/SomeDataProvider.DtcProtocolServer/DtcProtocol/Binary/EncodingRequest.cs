namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
	struct EncodingRequest
	{
		public ushort Size;

		public MessageTypeEnum Type;

		public int ProtocolVersion;

		public EncodingEnum Encoding;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
		public string ProtocolType;
	}
}