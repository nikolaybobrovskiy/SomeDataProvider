#pragma warning disable 8602
#pragma warning disable CC0022

namespace SomeDataProvider.DataStorage.Test
{
	using System.Globalization;
	using System.Threading.Tasks;

	using Microsoft.Extensions.Logging;

	using NBLib.Logging.Serilog;

	using NUnit.Framework;

	using Serilog;
	using Serilog.Events;

	using SomeDataProvider.DataStorage.Fred;

	[TestFixture]
	public class FredServiceTests
	{
		static readonly ILoggerFactory LoggerFactory = ConfigureLogger(new LoggerFactory());

		[Test]
		public async Task TestGetSeriesInfoAsync()
		{
			// TODO: From secret.
			using var svc = new Service((ServiceApiKey)"5e34dec427a5c32c3e45a70604b85459", LoggerFactory);
			var seriesInfo = await svc.GetSeriesInfoAsync("RUSCPIALLMINMEI");
			Assert.IsNotNull(seriesInfo);
			Assert.AreEqual("RUSCPIALLMINMEI", seriesInfo.Id);
			Assert.IsNotNull(seriesInfo.Title);
			Assert.IsNotEmpty(seriesInfo.Title);
			Assert.IsNotNull(seriesInfo.FrequencyShort);
			Assert.IsNotEmpty(seriesInfo.FrequencyShort);
			Assert.IsNotNull(seriesInfo.UnitsShort);
			Assert.IsNotEmpty(seriesInfo.UnitsShort);
			Assert.IsNotNull(seriesInfo.SeasonalAdjustmentShort);
			Assert.IsNotEmpty(seriesInfo.SeasonalAdjustmentShort);
		}

		[Test]
		public async Task TestGetObservationsAsync()
		{
			// TODO: From secret.
			using var svc = new Service((ServiceApiKey)"5e34dec427a5c32c3e45a70604b85459", LoggerFactory);
			var observationsData = await svc.GetObservationsAsync("RUSCPIALLMINMEI", limit: 10, transformation: DataValueTransformation.PercentChangeFromYearAgo);
			Assert.IsNotNull(observationsData);
			Assert.AreEqual(10, observationsData.Observations.Length);
			Assert.AreEqual("1992-01-01", observationsData.Observations[0].Date.ToString(Service.DateFormat, CultureInfo.InvariantCulture));
			Assert.AreEqual("1992-10-01", observationsData.Observations[^1].Date.ToString(Service.DateFormat, CultureInfo.InvariantCulture));
			Assert.IsNull(observationsData.Observations[0].Value);
			Assert.IsNull(observationsData.Observations[^1].Value);
			observationsData = await svc.GetObservationsAsync("RUSCPIALLMINMEI", limit: 10, offset: 10, transformation: DataValueTransformation.PercentChangeFromYearAgo);
			Assert.AreEqual(10, observationsData.Observations.Length);
			Assert.AreEqual("1992-11-01", observationsData.Observations[0].Date.ToString(Service.DateFormat, CultureInfo.InvariantCulture));
			Assert.AreEqual("1993-08-01", observationsData.Observations[^1].Date.ToString(Service.DateFormat, CultureInfo.InvariantCulture));
			Assert.IsNull(observationsData.Observations[0].Value);
			Assert.AreEqual(956.59373, observationsData.Observations[^1].Value);
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