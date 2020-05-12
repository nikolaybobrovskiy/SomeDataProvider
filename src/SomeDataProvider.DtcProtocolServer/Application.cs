// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

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

	using SomeDataProvider.DtcProtocolServer.Terminal;

	[Subcommand(typeof(StartCommand))]
	class Application : AbstractCommand, IAppLogLevelProvider
	{
		[Option("--log-level", Description = "Log level (verbose/debug/information/warning/error/critical). Default = information.")]
		public AppLogLevel? LogLevel { get; set; }

		[Command("start", FullName = "Start command", Description = "Starts DTC protocol server.")]
		class StartCommand : CommandWithLogger<StartCommand>
		{
			readonly ILoggerFactory _loggerFactory;
			IGui _gui;

			public StartCommand(IGui gui, ILoggerFactory loggerFactory)
				: base(loggerFactory)
			{
				_gui = gui;
				_loggerFactory = loggerFactory;
			}

			[Option("--port", Description = "Main server port. Default is 50001.")]
			public int Port { get; set; } = 50001;

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
							var mainServer = new Server(IPAddress.Any, Port, _loggerFactory);
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