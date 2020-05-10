// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;
	using System.Runtime.InteropServices;
	using System.Text;

	using NBLib.BuiltInTypes;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	readonly struct VariableLengthStringField
	{
		public readonly ushort Offset;
		public readonly ushort Length;

		const int MaxLength = 4096;

		public VariableLengthStringField(ushort offset, ushort length)
		{
			Offset = offset;
			Length = length;
		}

		public string GetStringValue(ReadOnlySpan<byte> buffer)
		{
			if (buffer.Length == 0)
			{
				return string.Empty;
			}
			return Encoding.ASCII.GetString(buffer.Slice(Offset, Length > MaxLength ? MaxLength : Length));
		}

		public VariableLengthStringField NewStringValue(string val)
		{
			// Ideally need to subtract prev value.
			if (val.IsEmpty())
			{
				if (Length == 0)
				{
					return this;
				}
			}
			var length = Convert.ToUInt16(val.Length + 1);
			if (Length != 0)
			{
				if (length == Length)
				{
					// Just replace buffer values.
					return this;
				}
				// Remove from the buffer and add to the end.
			}
			return new VariableLengthStringField(0, length);
		}
	}
}