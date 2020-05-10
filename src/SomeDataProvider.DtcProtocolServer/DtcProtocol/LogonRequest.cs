namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	class LogonRequest
	{
		public LogonRequest(int heartbeatIntervalInSeconds)
		{
			HeartbeatIntervalInSeconds = heartbeatIntervalInSeconds;
		}

		public int HeartbeatIntervalInSeconds { get; }
	}
}