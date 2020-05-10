namespace SomeDataProvider.DtcProtocolServer.Terminal
{
	interface IGui
	{
		delegate void QuitCommandHandler();

		event QuitCommandHandler? OnQuitCommand;

		void Start();

		void Stop();
	}
}