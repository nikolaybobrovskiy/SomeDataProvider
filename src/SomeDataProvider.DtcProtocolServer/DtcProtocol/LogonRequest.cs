// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	class LogonRequest
	{
		public LogonRequest(int heartbeatIntervalInSeconds, string clientName, string hardwareIdentifier)
		{
			HeartbeatIntervalInSeconds = heartbeatIntervalInSeconds;
			ClientName = clientName;
			HardwareIdentifier = hardwareIdentifier;
		}

		public int HeartbeatIntervalInSeconds { get; }

		public string ClientName { get; }

		public string HardwareIdentifier { get; }
	}
}