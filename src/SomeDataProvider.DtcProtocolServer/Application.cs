#pragma warning disable CC0022 // Should dispose object
namespace SomeDataProvider.DtcProtocolServer
{
	using System;
	using System.ComponentModel.DataAnnotations;
	using System.Context;
	using System.Net;
	using System.Threading;
	using System.Threading.Tasks;

	using McMaster.Extensions.CommandLineUtils;

	using Microsoft.Extensions.Logging;

	using NBLib.Cli;

	[Subcommand(typeof(StartCommand))]
	class Application : AbstractCommand, IAppLogLevelProvider
	{
		[Option("--log-level", Description = "Log level (verbose/debug/information/warning/error/critical). Default = information.")]
		public AppLogLevel? LogLevel { get; set; }

		[Command("start", FullName = "Start command", Description = "Starts DTC protocol server.")]
		class StartCommand : CommandWithLogger<StartCommand>
		{
			readonly IConsoleCommandsInterpreter _consoleCommandsInterpreter;
			private readonly ILoggerFactory _loggerFactory;

			public StartCommand(IConsoleCommandsInterpreter consoleCommandsInterpreter, ILoggerFactory loggerFactory)
				: base(loggerFactory)
			{
				_consoleCommandsInterpreter = consoleCommandsInterpreter;
				_loggerFactory = loggerFactory;
			}

			[Option("--port", Description = "Main server port. Default is 50001.")]
			public int Port { get; set; } = 50001;

			[Option("--history-port", Description = "History server port. Default is 50002.")]
			public int HistoryPort { get; set; } = 50002;

			public override Task<int> OnExecuteAsync(CommandLineApplication app)
			{
				var cts = new CancellationTokenSource();
				using (RunContext.WithCancel(cts.Token, "Program exit requested.", CancellationReason.ApplicationExit))
				{
					var mainServer = new Main.Server(IPAddress.Any, Port, _loggerFactory);
					L.LogInformation("Starting server...");
					mainServer.Start();
					_consoleCommandsInterpreter.OnQuitCommand += () =>
					{
						cts.Cancel();
					};
					_consoleCommandsInterpreter.Start();
					GetContext.CancellationToken.WaitHandle.WaitOne();
					L.LogInformation("Stopping server...");
					mainServer.Stop();
					return Task.FromResult(0);
				}
			}
		}
	}
}