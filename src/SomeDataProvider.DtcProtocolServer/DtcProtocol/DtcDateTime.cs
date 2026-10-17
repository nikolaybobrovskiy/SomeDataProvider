// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using System;

	static class DtcDateTime
	{
		/// <summary>
		/// Converts a date-time to DTC's <c>t_DateTimeWithMicrosecondsInt</c>: microseconds since the Unix epoch, UTC.
		/// Times before 1970 are negative. Unlike NBLib's <c>ToUnixTimeStamp</c>, nothing is clamped to 0.
		/// </summary>
		/// <remarks>The value is converted with <see cref="DateTime.ToUniversalTime"/>, as NBLib's conversion does, so a
		/// <see cref="DateTimeKind.Unspecified"/> value is taken as local time.</remarks>
		public static long ToUnixMicroseconds(DateTime dateTime)
		{
			var ticks = dateTime.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks;
			var microseconds = ticks / TimeSpan.TicksPerMicrosecond;
			// Round toward negative infinity, so that a time before 1970 is never moved later.
			if (ticks % TimeSpan.TicksPerMicrosecond < 0)
				microseconds--;
			return microseconds;
		}
	}
}
