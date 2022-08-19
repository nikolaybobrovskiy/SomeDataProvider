// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

#pragma warning disable CC0022

namespace SomeDataProvider.DtcProtocolServer
{
	using System;
	using System.Threading.Tasks;

	using Microsoft.Extensions.Configuration;
	using Microsoft.Extensions.DependencyInjection;
	using Microsoft.Extensions.Logging;

	using NBLib.Cli;
	using NBLib.Cli.Extensions;
	using NBLib.Configuration;

	using Serilog;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DtcProtocolServer.Data;

	class Program
	{
		static async Task<int> Main(string[] args)
		{
			using var appBuilder = new AppBuilder(args);
			using var appCtx = appBuilder.Build();
			var app = appCtx.CommandLineApplication;
			app.Name = "SomeDataProvider.DtcProtocolServer.exe";
			return await app.ExecuteWithDefaultContextAsync(args);
		}

		sealed class AppBuilder : DefaultAppBuilder<Application>, IDisposable
		{
			readonly object _storesProviderLock = new ();

			StoresProvider? _storesProvider;

			// https://medium.com/volosoft/asp-net-core-dependency-injection-best-practices-tips-tricks-c6e9c67f9d96
			public AppBuilder(string[] cliArgs)
				: base(cliArgs)
			{
			}

			public void Dispose()
			{
				_storesProvider?.Dispose();
			}

			protected override void Configure(IConfigurationBuilder configurationBuilder)
			{
				base.Configure(configurationBuilder);
				configurationBuilder.AddJsonFile("appsettings.json", optional: true);
			}

			protected override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
			{
				base.ConfigureServices(services, configuration);
				services.AddSingleton<ISymbolsStoreProvider>(GetOrCreateStoresProvider);
				services.AddSingleton<ISymbolHistoryStoreProvider>(GetOrCreateStoresProvider);
				services.Configure<StoresProvider.StoresOptions>(configuration.GetSection("StoresOptions"));
				//// services.AddSingleton(CreateSymbolHistoryStoreCache);
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

			StoresProvider GetOrCreateStoresProvider(IServiceProvider serviceProvider)
			{
				if (_storesProvider != null) return _storesProvider;
				lock (_storesProviderLock)
				{
					if (_storesProvider != null) return _storesProvider;
					// ReSharper disable once PossibleMultipleWriteAccessInDoubleCheckLocking
					return _storesProvider = new StoresProvider(
						serviceProvider.GetConfiguration<StoresProvider.StoresOptions>(),
						serviceProvider.GetRequiredService<ILoggerFactory>());
				}
			}

			//// static ISymbolHistoryStoreCache CreateSymbolHistoryStoreCache(IServiceProvider serviceProvider)
			//// {
			//// 	var result = new SymbolHistoryStoreXpoCache("HistoryDataCache.db", serviceProvider.GetRequiredService<ILoggerFactory>());
			//// 	result.InitAsync().GetAwaiter().GetResult();
			//// 	return result;
			//// }
		}
	}
}