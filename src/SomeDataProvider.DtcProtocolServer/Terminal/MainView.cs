namespace SomeDataProvider.DtcProtocolServer.Terminal
{
	using System.Reflection;

	using global::Terminal.Gui;

	using NBLib.TerminalGui;

	// https://sirwan.info/archive/2018/05/02/Developing-Console-based-UI-in-C/
	// https://itnext.io/terminal-console-user-interface-in-net-core-4e978f1225b
	sealed class MainView : MainViewBase<MainView>
	{
		readonly LogWindow _logWindow;

		public MainView()
		{
			Add(new AppTitle($"Some Data Provider DTC protocol server v{Assembly.GetExecutingAssembly().GetName().Version}"));
			Add(CreateMainMenu());
			_logWindow = new LogWindow("Logs")
			{
				Y = 2,
				Height = Dim.Fill(),
				Width = Dim.Fill(),
			};
			Add(_logWindow);
		}

		public void WriteLog(string log)
		{
			_logWindow.WriteLog(log);
		}

		static bool ConfirmQuit()
		{
			return MessageBox.Query(60, 7, "Quit Server", "Are you sure you want to stop the server and quit?", "Yes", "No") == 0;
		}

		MenuBar CreateMainMenu()
		{
			return new MenuBar(new[]
			{
				new MenuBarItem("_File", new[]
				{
					new MenuItem("_Quit", string.Empty, () =>
					{
						if (ConfirmQuit())
						{
							OnQuitRequested();
						}
					}),
				})
			})
			{
				Y = 1
			};
		}
	}
}