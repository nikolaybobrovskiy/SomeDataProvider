#pragma warning disable CC0022 // Should dispose object: Rule misbehaves after C# 8 "using" syntax usage.
#pragma warning disable CC0033

namespace SomeDataProvider.DataStorage.Xpo.Test
{
	using System;
	using System.IO;
	using System.Threading.Tasks;

	using DevExpress.Xpo.DB;

	using Microsoft.Extensions.Logging;

	using NBLib.Logging.Serilog;

	using NUnit.Framework;

	using Serilog;
	using Serilog.Events;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DataStorage.InMem;

	using LoggerExtensions = NBLib.Logging.LoggerExtensions;

	[TestFixture]
	public class SymbolHistoryStoreXpoCacheTests
	{
		const string TestDatabasePath = "c:\\temp\\SomeDataProviderTestData.db";

		static readonly ILoggerFactory LoggerFactory = ConfigureLogger(new LoggerFactory());
		static readonly Lazy<ILogger<SymbolHistoryStoreXpoCacheTests>> LazyLogger = new Lazy<ILogger<SymbolHistoryStoreXpoCacheTests>>(() => LoggerFactory.CreateLogger<SymbolHistoryStoreXpoCacheTests>(), true);

		static LoggerExtensions.OperationLogger? currentTestContext;

		static ILogger<SymbolHistoryStoreXpoCacheTests> L => LazyLogger.Value;

		[OneTimeSetUp]
		public void OneTimeSetUp()
		{
		}

		[OneTimeTearDown]
		public void OneTimeTearDown()
		{
			LoggerFactory.Dispose();
		}

		[SetUp]
		public void SetUp()
		{
			currentTestContext = new LoggerExtensions.OperationLogger(L, TestContext.CurrentContext.Test.FullName);
		}

		[TearDown]
		public void TearDown()
		{
			currentTestContext?.Dispose();
		}

		[Test]
		public void TestDataBaseCreation()
		{
			File.Delete(TestDatabasePath);
			var connectionString = SQLiteConnectionProvider.GetConnectionString(TestDatabasePath);
			using var dataLayer = ConnectionHelper.CreateDataLayer(connectionString);
			Assert.IsTrue(File.Exists(TestDatabasePath));
		}

		[Test]
		public async Task TestSymbolHistoryStoreCacheEntryGetAndCreate()
		{
			using SymbolsStore store = new SymbolsStore();
			ISymbol symbol = (await store.GetSymbolAsync("fred-RUSCPIALLMINMEI.pc1"))!;

			File.Delete(TestDatabasePath);
			var connectionString = SQLiteConnectionProvider.GetConnectionString(TestDatabasePath);
			using var dataLayer = ConnectionHelper.CreateDataLayer(connectionString);
			var cache = new SymbolHistoryStoreXpoCache(dataLayer, LoggerFactory);
			var entry = await cache.GetSymbolHistoryStoreCacheEntryAsync(symbol, HistoryInterval.Daily);
			Assert.IsNull(entry);
			Assert.AreEqual(0, await cache.GetSymbolHistoryStoreCacheEntriesCountAsync());
			entry = await cache.GetOrCreateSymbolHistoryStoreCacheEntryAsync(symbol, HistoryInterval.Daily);
			Assert.IsNotNull(entry);
			Assert.AreEqual(1, await cache.GetSymbolHistoryStoreCacheEntriesCountAsync());
			entry = await cache.GetSymbolHistoryStoreCacheEntryAsync(symbol, HistoryInterval.Daily);
			Assert.IsNotNull(entry);
			var entry2 = await cache.GetOrCreateSymbolHistoryStoreCacheEntryAsync(symbol, HistoryInterval.Daily);
			Assert.IsNotNull(entry2);
			Assert.AreEqual(1, await cache.GetSymbolHistoryStoreCacheEntriesCountAsync());
			Assert.AreEqual((int)entry!.GetType().GetProperty("Id")!.GetValue(entry)!, (int)entry2!.GetType().GetProperty("Id")!.GetValue(entry2)!);
		}

		static ILoggerFactory ConfigureLogger(ILoggerFactory loggerFactory)
		{
			var serilogCfg = new LoggerConfiguration();
			serilogCfg
				.MinimumLevel.Is(LogEventLevel.Verbose)
				.Enrich.FromLogContext()
				.Enrich.WithProcessId()
				.Enrich.WithThreadId()
				.Enrich.With<SystemContextEnricher>()
				.Enrich.With<SequentialIdEnricher>()
				.Enrich.With<ApplicationInstanceIdEnricher>();
			serilogCfg.WriteTo.Logger(cfg => cfg
				.WriteTo.Seq("http://localhost:5341", batchPostingLimit: 300));
			var serilog = serilogCfg.CreateLogger();
			loggerFactory.AddSerilog(serilog, true);
			var xpoLogger = new NBLib.Logging.Xpo.XpoLogger(loggerFactory);
			NBLib.Logging.Xpo.XpoLogger.LogStackTraceForDbCommand = true;
			NBLib.Logging.Xpo.XpoLogger.LogStackTraceForSessionEvent = true;
			DevExpress.Xpo.Logger.LogManager.IncludeStackTrace = true;
			DevExpress.Xpo.Logger.LogManager.SetTransport(xpoLogger);

			return loggerFactory;
		}
	}
}