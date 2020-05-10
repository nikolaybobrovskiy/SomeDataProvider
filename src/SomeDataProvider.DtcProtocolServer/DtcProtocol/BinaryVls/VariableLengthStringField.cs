namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;
	using System.Runtime.InteropServices;
	using System.Text;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	readonly struct VariableLengthStringField
	{
		public readonly ushort Offset;
		public readonly ushort Length;

		const int MaxLength = 4096;

		public string GetStringValue(ReadOnlySpan<byte> buffer)
		{
			if (buffer.Length == 0)
			{
				return string.Empty;
			}
			return Encoding.ASCII.GetString(buffer.Slice(Offset, Length > MaxLength ? MaxLength : Length));
		}
	}
}