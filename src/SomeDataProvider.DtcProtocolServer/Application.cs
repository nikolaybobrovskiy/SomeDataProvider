namespace SomeDataProvider.DtcProtocolServer
{
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
			public StartCommand(ILoggerFactory loggerFactory)
				: base(loggerFactory)
			{
			}

			public override Task<int> OnExecuteAsync(CommandLineApplication app)
			{
				throw new System.NotImplementedException();
			}
		}
	}
}