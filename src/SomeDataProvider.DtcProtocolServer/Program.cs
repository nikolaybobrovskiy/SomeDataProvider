// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

#pragma warning disable CC0022

namespace SomeDataProvider.DtcProtocolServer
{
	using System.Context;
	using System.Threading.Tasks;

	using Microsoft.Extensions.DependencyInjection;

	using NBLib.Cli;
	using NBLib.Configuration;

	using Serilog;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DataStorage.HistoryStores;

	class Program
	{
		static async Task<int> Main(string[] args)
		{
			var appBuilder = new AppBuilder();
			using var appCtx = appBuilder.Build();
			var app = appCtx.CommandLineApplication;
			app.Name = "SomeDataProvider.DtcProtocolServer.exe";
			using (RunContext.WithValue(GetContext.ApplicationNameProperty, "SomeDataProvider.DtcProtocolServer"))
			{
				return await app.ExecuteAsync(args);
			}
		}

		class AppBuilder : DefaultAppBuilder<Application>
		{
			// https://medium.com/volosoft/asp-net-core-dependency-injection-best-practices-tips-tricks-c6e9c67f9d96
			protected override void ConfigureServices(IServiceCollection services)
			{
				base.ConfigureServices(services);
				services.AddSingleton<ISymbolsStore, DataStorage.InMem.SymbolsStore>();
				services.AddSingleton<ISymbolHistoryStoreInstanceFactory, SymbolHistoryStoreInstanceFactory>();
				services.Configure<SymbolHistoryTextFileStore.Options>((provider, opts) =>
				{
					// TODO: Implement via JSON file.
					opts.FolderPath = @"i:\Projects\SomeDataProvider\data";
				});
			}

			protected override void ConfigureLogger(Application app, LoggerConfiguration serilogCfg)
			{
				serilogCfg.WriteTo.Console(
					outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} {ThreadId:d8}{OperationStartEnd:l}{Duration:l} {Message}{NewLine}{Exception}",
					theme: ConsoleLogTheme.Default);
				if (app.LogToLocalSeq)
				{
					serilogCfg.WriteTo.Logger(cfg => cfg
						.WriteTo.Seq("http://localhost:5341", batchPostingLimit: 300));
				}
			}
		}
	}
}