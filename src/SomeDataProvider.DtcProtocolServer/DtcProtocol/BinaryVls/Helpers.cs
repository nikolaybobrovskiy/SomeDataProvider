namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;

	static class Helpers
	{
		public static ushort GetVlsFieldLength(this string? str)
		{
			if (str == null) return 0;
			if (str.Length == 0) return 0;
			return Convert.ToUInt16(str.Length + 1);
		}
	}
}