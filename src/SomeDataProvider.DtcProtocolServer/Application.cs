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
	using SomeDataProvider.DtcProtocolServer.Terminal;

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
			IGui _gui;

			public StartCommand(IGui gui, ISymbolsStore symbolsStore, ILoggerFactory loggerFactory)
				: base(loggerFactory)
			{
				_gui = gui;
				_symbolsStore = symbolsStore;
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
						_gui.Start();
						_gui.OnQuitCommand += () =>
						{
							cts.Cancel();
						};
						try
						{
							var mainServer = new Server(IPAddress.Any, Port, OnlyHistoryServer, _symbolsStore, _loggerFactory);
							L.LogInformation("Starting server on {listenEndpoint}...", mainServer.Endpoint);
							mainServer.Start();
							GetContext.CancellationToken.WaitHandle.WaitOne();
							L.LogInformation("Stopping server...");
							mainServer.Stop();
							return Task.FromResult(0);
						}
						finally
						{
							_gui.Stop();
						}
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