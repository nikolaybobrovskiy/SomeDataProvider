namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
	readonly struct Heartbeat
	{
		public readonly ushort Size;
		public readonly MessageTypeEnum Type;
		public readonly uint NumDroppedMessages;
		public readonly long CurrentDateTime;

		public Heartbeat(uint numDroppedMessages)
			: this()
		{
			Size = Convert.ToUInt16(Marshal.SizeOf(this));
			NumDroppedMessages = numDroppedMessages;
		}
	}
}