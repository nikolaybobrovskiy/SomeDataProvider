namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
	readonly struct EncodingRequest
	{
		public readonly ushort Size;

		public readonly MessageTypeEnum Type;

		public readonly int ProtocolVersion;

		public readonly EncodingEnum Encoding;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
		public readonly string ProtocolType;
	}
}