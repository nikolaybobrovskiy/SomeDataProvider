// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

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

	using SomeDataProvider.DataStorage.Definitions;
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

			// https://medium.com/volosoft/asp-net-core-dependency-injection-best-practices-tips-tricks-c6e9c67f9d96
			protected override void ConfigureServices(IServiceCollection services)
			{
				base.ConfigureServices(services);
				services.AddSingleton<IGui>(_ => _gui = new Gui());
				services.AddSingleton<ISymbolsStore, DataStorage.InMem.SymbolsStore>();
			}

			protected override void ConfigureLogger(Application app, LoggerConfiguration serilogCfg)
			{
				serilogCfg.WriteTo.Console(
					outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}{OperationStartEnd:l}{Duration:l} {Message}{NewLine}{Exception}",
					theme: ConsoleLogTheme.Default,
					outputStreamSelector: () => _gui != null ? _consoleLogStreamWriter : null);
				if (app.LogToLocalSeq)
				{
					serilogCfg.WriteTo.Logger(cfg => cfg
						.WriteTo.Seq("http://localhost:5341", batchPostingLimit: 300));
				}
			}
		}
	}
}