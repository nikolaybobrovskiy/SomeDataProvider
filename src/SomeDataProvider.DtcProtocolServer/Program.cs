namespace SomeDataProvider.DtcProtocolServer
{
	using System.Threading.Tasks;

	using NBLib.Cli;

	class Program
	{
		static async Task<int> Main(string[] args)
		{
			using var appCtx = new AppBuilder().Build();
			var app = appCtx.CommandLineApplication;
			app.Name = "SomeDataProvider.DtcProtocolServer.exe";
			return await app.ExecuteAsync(args);
		}

		class AppBuilder : DefaultAppBuilder<Application>
		{
		}
	}
}