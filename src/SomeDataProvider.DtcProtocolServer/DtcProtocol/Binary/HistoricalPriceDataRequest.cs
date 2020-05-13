namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	readonly struct HistoricalPriceDataRequest
	{
		public readonly ushort Size;
		public readonly MessageTypeEnum Type;
	}
}