namespace SomeDataProvider.DtcProtocolServer
{
	using System.Context;
	using System.Threading.Tasks;

	using Microsoft.Extensions.DependencyInjection;

	using NBLib.Cli;

	class Program
	{
		static async Task<int> Main(string[] args)
		{
			using var appCtx = new AppBuilder().Build();
			var app = appCtx.CommandLineApplication;
			app.Name = "SomeDataProvider.DtcProtocolServer.exe";
			using (RunContext.WithValue(GetContext.ApplicationNameProperty, "SomeDataProvider.DtcProtocolServer"))
			{
				return await app.ExecuteAsync(args);
			}
		}

		class AppBuilder : DefaultAppBuilder<Application>
		{
			protected override void ConfigureServices(IServiceCollection services)
			{
				base.ConfigureServices(services);
				services.AddSingleton<IConsoleCommandsInterpreter, ConsoleCommandsInterpreter>();
			}
		}
	}
}