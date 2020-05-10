#pragma warning disable CC0022

namespace SomeDataProvider.DtcProtocolServer
{
	using System;
	using System.Context;
	using System.IO;
	using System.Threading.Tasks;

	using Microsoft.Extensions.DependencyInjection;

	using NBLib.BuiltInTypes;
	using NBLib.Cli;

	using Serilog;
	using Serilog.Sinks.SystemConsole.Themes;

	using SomeDataProvider.DtcProtocolServer.Terminal;

	class Program
	{
		static async Task<int> Main(string[] args)
		{
			using var appBuilder = new AppBuilder();
			using var appCtx = appBuilder.Build();
			var app = appCtx.CommandLineApplication;
			app.Name = "SomeDataProvider.DtcProtocolServer.exe";
			using (RunContext.WithValue(GetContext.ApplicationNameProperty, "SomeDataProvider.DtcProtocolServer"))
			{
				return await app.ExecuteAsync(args);
			}
		}

		class AppBuilder : DefaultAppBuilder<Application>, IDisposable, StringsStream.IStringsReceiver
		{
			StreamWriter _consoleLogStreamWriter;
			volatile Gui? _gui;

			public AppBuilder()
			{
				_consoleLogStreamWriter = new StreamWriter(new StringsStream(this));
			}

			public void Dispose()
			{
				_consoleLogStreamWriter.Dispose();
			}

			void StringsStream.IStringsReceiver.ReceiveString(string str)
			{
				if (_gui == null) return;
				if (str.IsEmpty()) return;
				_gui.WriteLog(str);
			}

			protected override void ConfigureServices(IServiceCollection services)
			{
				base.ConfigureServices(services);
				services.AddSingleton<IGui>(_ => _gui = new Gui());
			}

			protected override void ConfigureLogger(LoggerConfiguration serilogCfg)
			{
				serilogCfg.WriteTo.Console(
					outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}{OperationStartEnd:l}{Duration:l} {Message}{NewLine}{Exception}",
					theme: ConsoleLogTheme.Default,
					outputStreamSelector: () => _gui != null ? _consoleLogStreamWriter : null);
			}
		}
	}
}