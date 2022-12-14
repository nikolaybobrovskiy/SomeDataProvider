// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums
{
	enum EncodingEnum
	{
		BinaryEncoding = 0,
		BinaryWithVariableLengthStrings = 1,
		JsonEncoding = 2,
		JsonCompactEncoding = 3,
		ProtocolBuffers = 4
	}
}