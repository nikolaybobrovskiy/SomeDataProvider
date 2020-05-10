namespace SomeDataProvider.DtcProtocolServer.Terminal
{
	using System;
	using System.Threading;

	using TerminalGuiApplication = global::Terminal.Gui.Application;

	class Gui : IGui
	{
		volatile MainView? _mainView;

		public event IGui.QuitCommandHandler? OnQuitCommand;

		public void Start()
		{
			Console.CancelKeyPress += OnConsoleCancelKeyPress;
			new Thread(() =>
			{
				TerminalGuiApplication.Init();
				_mainView = new MainView();
				_mainView.QuitRequested += () => OnQuitCommand?.Invoke();
				TerminalGuiApplication.Run(_mainView);
			}).Start();
			SpinWait.SpinUntil(() => _mainView != null);
		}

		public void Stop()
		{
			if (_mainView == null) return;
			TerminalGuiApplication.MainLoop.Invoke(() =>
			{
				_mainView.Running = false;
			});
		}

		public void WriteLog(string log)
		{
			if (_mainView == null) return;
			TerminalGuiApplication.MainLoop.Invoke(() =>
			{
				_mainView.WriteLog(log);
			});
		}

		void OnConsoleCancelKeyPress(object sender, ConsoleCancelEventArgs e)
		{
			e.Cancel = true;
			OnQuitCommand?.Invoke();
		}
	}
}