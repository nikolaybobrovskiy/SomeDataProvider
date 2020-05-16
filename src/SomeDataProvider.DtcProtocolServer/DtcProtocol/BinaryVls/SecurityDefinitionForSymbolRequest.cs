// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	readonly struct SecurityDefinitionForSymbolRequest
	{
		public readonly ushort Size;
		public readonly MessageTypeEnum Type;
		public readonly ushort BaseSize;
		public readonly int RequestId;
		public readonly VariableLengthStringField Symbol;
		public readonly VariableLengthStringField Exchange;
	}
}