#pragma warning disable 8602
#pragma warning disable CC0022

namespace SomeDataProvider.DataStorage.Test
{
	using System.Threading.Tasks;

	using Microsoft.Extensions.Logging;

	using NBLib.BuiltInTypes;
	using NBLib.Logging.Serilog;

	using NUnit.Framework;

	using Serilog;
	using Serilog.Events;

	[TestFixture]
	public class SymbolsStoreTests
	{
		static readonly ILoggerFactory LoggerFactory = ConfigureLogger(new LoggerFactory());

		//// [Test]
		//// public async Task TestGetSymbolAsync()
		//// {
		//// 	using SymbolsStore store = new(LoggerFactory);
		//// 	var symbol = await store.GetSymbolAsync("fred-RUSCPIALLMINMEI.pc1");
		//// 	Assert.IsNotNull(symbol);
		//// 	Assert.IsFalse(symbol.Description.IsEmpty());
		//// 	Assert.IsTrue(symbol.Description.Contains("% Chg y/y"));
		//// }

		[Test]
		public async Task TestFredGetSymbolAsync()
		{
			using var store = new Fred.Store(FredTestApiKey.GetOrSkip(), LoggerFactory);
			var symbol = await store.GetSymbolAsync("RUSCPIALLMINMEI.pc1");
			Assert.IsNotNull(symbol);
			Assert.IsFalse(symbol.Description.IsEmpty());
			Assert.IsTrue(symbol.Description.Contains("% Chg y/y"));
		}

		static ILoggerFactory ConfigureLogger(ILoggerFactory loggerFactory)
		{
			var serilogCfg = new LoggerConfiguration();
			serilogCfg
				.MinimumLevel.Is(LogEventLevel.Verbose)
				.Enrich.FromLogContext()
				.Enrich.WithThreadId()
				.Enrich.With<SystemContextEnricher>()
				.Enrich.With<SequentialIdEnricher>()
				.Enrich.With<ApplicationInstanceIdEnricher>();
			serilogCfg.WriteTo.Logger(cfg => cfg
				.WriteTo.Seq("http://localhost:5341", batchPostingLimit: 300));
			var serilog = serilogCfg.CreateLogger();
			loggerFactory.AddSerilog(serilog, true);
			return loggerFactory;
		}
	}
}