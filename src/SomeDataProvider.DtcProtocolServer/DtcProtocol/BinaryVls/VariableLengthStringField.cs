namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System.Runtime.InteropServices;

	[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
	struct VariableLengthStringField
	{
		public ushort Offset;
		public ushort Length;
	}
}