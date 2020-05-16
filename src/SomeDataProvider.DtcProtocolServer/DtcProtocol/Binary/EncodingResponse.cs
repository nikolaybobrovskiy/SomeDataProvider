// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

// ReSharper disable FieldCanBeMadeReadOnly.Global
namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	struct EncodingResponse
	{
		public ushort Size;

		public MessageTypeEnum Type;

		public int ProtocolVersion;

		public EncodingEnum Encoding;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
		public string ProtocolType;

		public EncodingResponse(EncodingEnum encoding)
			: this()
		{
			Size = Convert.ToUInt16(Marshal.SizeOf(this));
			Type = MessageTypeEnum.EncodingResponse;
			ProtocolType = "DTC";
			Encoding = encoding;
			ProtocolVersion = MessageProtocol.Version;
		}
	}
}