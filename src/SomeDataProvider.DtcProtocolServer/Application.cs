// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

// ReSharper disable UnusedAutoPropertyAccessor.Local
// ReSharper disable MemberCanBePrivate.Local

#pragma warning disable CC0022 // Should dispose object
namespace SomeDataProvider.DtcProtocolServer
{
	using System;
	using System.Context;
	using System.Net;
	using System.Threading.Tasks;

	using McMaster.Extensions.CommandLineUtils;

	using Microsoft.Extensions.Logging;

	using NBLib.Cli;
	using NBLib.Exceptions;

	using SomeDataProvider.DataStorage.Definitions;

	// How to set up Sierra Chart for custom data provider: https://www.sierrachart.com/index.php?page=doc/DTC_TestClient.php

	[Subcommand(typeof(StartCommand))]
	class Application : AbstractCommand, IAppLogLevelProvider
	{
		[Option("--log-level", Description = "Log level (verbose/debug/information/warning/error/critical). Default = information.")]
		public AppLogLevel? LogLevel { get; set; }

		[Option("--log-to-local-seq", Description = "Log to local Seq endpoint.")]
		public bool LogToLocalSeq { get; set; }

		[Command("start", FullName = "Start command", Description = "Starts DTC protocol server.")]
		class StartCommand : CommandWithLogger<StartCommand>
		{
			readonly ILoggerFactory _loggerFactory;
			readonly ISymbolsStoreProvider _symbolsStoreProvider;
			readonly ISymbolHistoryStoreProvider _historyStoreProvider;

			public StartCommand(
				ISymbolsStoreProvider symbolsStoreProvider,
				ISymbolHistoryStoreProvider historyStoreProvider,
				ILoggerFactory loggerFactory)
				: base(loggerFactory)
			{
				_symbolsStoreProvider = symbolsStoreProvider;
				_historyStoreProvider = historyStoreProvider;
				_loggerFactory = loggerFactory;
			}

			[Option("--port", Description = "Server port. Default is 50001.")]
			public int Port { get; set; } = 50001;

			[Option("--only-history", Description = "Enable only history server mode (enables compression, disables heartbeat, one connection per history request).")]
			public bool OnlyHistoryServer { get; set; }

			public override Task<int> OnExecuteAsync(CommandLineApplication app)
			{
				var ct = GetContext.CancellationToken;
				try
				{
					var mainServer = new Server(
						IPAddress.Any,
						Port,
						OnlyHistoryServer,
						_symbolsStoreProvider,
						_historyStoreProvider,
						_loggerFactory);
					L.LogInformation("Starting server on {listenEndpoint}...", mainServer.Endpoint);
					mainServer.Start();
					ct.WaitHandle.WaitOne();
					L.LogInformation("Stopping server...");
					mainServer.Stop();
					return Task.FromResult(0);
				}
				catch (Exception ex) when (!ex.IsExplainedCancellation())
				{
					L.LogCritical(ex, ex.Message);
					return Task.FromResult(-1);
				}
			}
		}

		[Command("fillsymbols", FullName = "Fill symbols", Description = "Fills symbols settings to SierraChart settings file.")]
		class FillSymbolsCommand : CommandWithLogger<StartCommand>
		{
			public FillSymbolsCommand(ILoggerFactory loggerFactory)
				: base(loggerFactory)
			{
			}

			public override Task<int> OnExecuteAsync(CommandLineApplication app)
			{
				throw new NotImplementedException();
			}
		}
	}
}