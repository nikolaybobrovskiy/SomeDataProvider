namespace SomeDataProvider.DtcProtocolServer.Main
{
	using NetCoreServer;

	public class Session : TcpSession
	{
		public Session(TcpServer server)
			: base(server)
		{
		}
	}
}