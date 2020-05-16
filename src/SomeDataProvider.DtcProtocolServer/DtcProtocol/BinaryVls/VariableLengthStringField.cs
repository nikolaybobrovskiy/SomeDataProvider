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
			if (Length <= 1)
			{
				return string.Empty;
			}
			return Encoding.ASCII.GetString(buffer.Slice(Offset, Length > MaxLength ? MaxLength : Length - 1));
		}

		public VariableLengthStringField CreateStringValue(string? val, byte[] stringsBuffer, ushort index)
		{
			if (Offset != default || Length != default)
			{
				throw new InvalidOperationException($"{nameof(VariableLengthStringField)} can be set only once.");
			}
			if (val.IsEmpty())
			{
				return this;
			}
			var result = new VariableLengthStringField(index, val.GetVlsFieldLength());
			foreach (var c in Encoding.ASCII.GetBytes(val))
			{
				stringsBuffer[index] = c;
				index++;
			}
			return result;
		}
	}
}