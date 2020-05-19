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
	using System.Threading;
	using System.Threading.Tasks;

	using McMaster.Extensions.CommandLineUtils;

	using Microsoft.Extensions.Logging;

	using NBLib.Cli;

	using SomeDataProvider.DataStorage.Definitions;

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
			readonly ISymbolsStore _symbolsStore;
			readonly ISymbolHistoryStoreInstanceFactory _historyStoreInstanceFactory;

			public StartCommand(
				ISymbolsStore symbolsStore,
				ISymbolHistoryStoreInstanceFactory historyStoreInstanceFactory,
				ILoggerFactory loggerFactory)
				: base(loggerFactory)
			{
				_symbolsStore = symbolsStore;
				_historyStoreInstanceFactory = historyStoreInstanceFactory;
				_loggerFactory = loggerFactory;
			}

			[Option("--port", Description = "Server port. Default is 50001.")]
			public int Port { get; set; } = 50001;

			[Option("--only-history", Description = "Enable only history server mode (enables compression, disables heartbeat, one connection per history request).")]
			public bool OnlyHistoryServer { get; set; }

			public override Task<int> OnExecuteAsync(CommandLineApplication app)
			{
				var cts = new CancellationTokenSource();
				using (RunContext.WithCancel(cts.Token, "Program exit requested.", CancellationReason.ApplicationExit))
				{
					try
					{
						Console.CancelKeyPress += (_, cancelKeyPressArgs) =>
						{
							cancelKeyPressArgs.Cancel = true;
							cts.Cancel();
						};
						var mainServer = new Server(
							IPAddress.Any,
							Port,
							OnlyHistoryServer,
							_symbolsStore,
							_historyStoreInstanceFactory,
							_loggerFactory);
						L.LogInformation("Starting server on {listenEndpoint}...", mainServer.Endpoint);
						mainServer.Start();
						GetContext.CancellationToken.WaitHandle.WaitOne();
						L.LogInformation("Stopping server...");
						mainServer.Stop();
						return Task.FromResult(0);
					}
					catch (Exception ex) when (!(ex is OperationCanceledException))
					{
						L.LogCritical(ex, ex.Message);
						return Task.FromResult(-1);
					}
				}
			}
		}
	}
}