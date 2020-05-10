// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums
{
	enum LogonStatusEnum
	{
		LogonSuccess = 1,
		LogonError = 2,
		LogonErrorNoReconnect = 3,
		LogonReconnectNewAddress = 4
	}
}