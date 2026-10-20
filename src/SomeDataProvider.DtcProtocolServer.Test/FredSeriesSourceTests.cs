// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Net;
	using System.Net.Http;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Web;

	using Dtc.Protocol;
	using Dtc.Protocol.Private;

	using Microsoft.Extensions.Logging.Abstractions;

	using NUnit.Framework;

	using SomeDataProvider.DataStorage.Fred;
	using SomeDataProvider.DtcProtocolServer.Data;

	/// <summary>
	/// The series extension answered from FRED (docs/dtc-series-extension.md §5): fred/series and fred/series/observations
	/// with output type 1 mapped by meaning, FRED's failures mapped to reject reasons, and paging. No test calls FRED: the
	/// store runs on a fake HttpMessageHandler with FRED's documented JSON.
	/// </summary>
	[TestFixture]
	public class FredSeriesSourceTests
	{
		const string Gdpc1Json = """
			{"realtime_start":"2026-10-19","realtime_end":"2026-10-19","seriess":[{"id":"GDPC1","realtime_start":"2026-10-19",
			"realtime_end":"2026-10-19","title":"Real Gross Domestic Product","observation_start":"1947-01-01",
			"observation_end":"2026-04-01","frequency":"Quarterly","frequency_short":"Q","units":"Billions of Chained 2017 Dollars",
			"units_short":"Bil. of Chn. 2017 $","seasonal_adjustment":"Seasonally Adjusted Annual Rate","seasonal_adjustment_short":"SAAR",
			"last_updated":"2026-09-25 07:51:02-05","popularity":91,"notes":"BEA Account Code: A191RX"}]}
			""";

		[Test]
		public async Task GetSeriesMetadataAsync_MapsFredsMetadataByMeaning()
		{
			var fred = new FakeFred(_ => FakeFredHandler.JsonResponse(Gdpc1Json));

			var metadata = await fred.Source().GetSeriesMetadataAsync("GDPC1", CancellationToken.None);

			Assert.That(metadata, Is.EqualTo(new SeriesMetadataResponse
			{
				Title = "Real Gross Domestic Product",
				Units = "Billions of Chained 2017 Dollars",
				UnitsShort = "Bil. of Chn. 2017 $",
				Frequency = SeriesFrequency.Quarterly,
				WeekEndDay = null,
				UnitsTransformation = SeriesUnitsTransformation.Levels,
				SeasonalAdjustment = SeriesSeasonalAdjustment.SeasonallyAdjustedAnnualRate,
				ObservationStart = new DateOnly(1947, 1, 1),
				ObservationEnd = new DateOnly(2026, 4, 1),
				// 07:51:02 at UTC-5 is 12:51:02 UTC.
				LastUpdated = DtcTimestamp.FromDateTimeOffset(new DateTimeOffset(2026, 9, 25, 12, 51, 2, TimeSpan.Zero)),
			}));
			Assert.That(fred.Requests.Single().AbsolutePath, Does.EndWith("/series"));
			Assert.That(Query(fred.Requests.Single())["series_id"], Is.EqualTo("GDPC1"));
		}

		// The suffix sets only the transformation; units and units short stay the base series', and FRED is asked for the base series.
		[TestCase("GDPC1.chg", SeriesUnitsTransformation.Change)]
		[TestCase("GDPC1.ch1", SeriesUnitsTransformation.ChangeFromYearAgo)]
		[TestCase("GDPC1.pch", SeriesUnitsTransformation.PercentChange)]
		[TestCase("GDPC1.pc1", SeriesUnitsTransformation.PercentChangeFromYearAgo)]
		[TestCase("GDPC1.pca", SeriesUnitsTransformation.CompoundedAnnualRateOfChange)]
		[TestCase("GDPC1.cch", SeriesUnitsTransformation.ContinuouslyCompoundedRateOfChange)]
		[TestCase("GDPC1.cca", SeriesUnitsTransformation.ContinuouslyCompoundedAnnualRateOfChange)]
		[TestCase("GDPC1.log", SeriesUnitsTransformation.NaturalLog)]
		public async Task GetSeriesMetadataAsync_MapsTheUnitsSuffix(string code, SeriesUnitsTransformation transformation)
		{
			var fred = new FakeFred(_ => FakeFredHandler.JsonResponse(Gdpc1Json));

			var metadata = await fred.Source().GetSeriesMetadataAsync(code, CancellationToken.None);

			Assert.That(metadata.UnitsTransformation, Is.EqualTo(transformation));
			Assert.That(metadata.Units, Is.EqualTo("Billions of Chained 2017 Dollars"));
			Assert.That(Query(fred.Requests.Single())["series_id"], Is.EqualTo("GDPC1"));
		}

		[TestCase("Daily", "D", SeriesFrequency.Daily, null)]
		[TestCase("Daily, Close", "D", SeriesFrequency.Daily, null)]
		[TestCase("Daily, 7-Day", "D", SeriesFrequency.DailyIncludingWeekends, null)]
		[TestCase("Weekly, Ending Friday", "W", SeriesFrequency.Weekly, DayOfWeek.Friday)]
		[TestCase("Weekly, Ending Saturday", "W", SeriesFrequency.Weekly, DayOfWeek.Saturday)]
		[TestCase("Weekly, As of Wednesday", "W", SeriesFrequency.Weekly, DayOfWeek.Wednesday)]
		[TestCase("Weekly", "W", SeriesFrequency.Weekly, null)]
		[TestCase("Biweekly, Ending Wednesday", "BW", SeriesFrequency.Biweekly, DayOfWeek.Wednesday)]
		[TestCase("Monthly", "M", SeriesFrequency.Monthly, null)]
		[TestCase("Quarterly", "Q", SeriesFrequency.Quarterly, null)]
		[TestCase("Semiannual", "SA", SeriesFrequency.Semiannual, null)]
		[TestCase("Annual", "A", SeriesFrequency.Annual, null)]
		[TestCase("Annual, Fiscal Year", "A", SeriesFrequency.Other, null)]
		[TestCase("Not Applicable", "NA", SeriesFrequency.Other, null)]
		[TestCase(null, null, SeriesFrequency.Other, null)]
		public void ToFrequency_MapsFredsFrequency(string? frequency, string? frequencyShort, SeriesFrequency expected, DayOfWeek? weekEndDay)
		{
			Assert.That(FredSeriesSource.ToFrequency(frequency, frequencyShort), Is.EqualTo((expected, weekEndDay)));
		}

		[TestCase("NSA", SeriesSeasonalAdjustment.NotSeasonallyAdjusted)]
		[TestCase("SA", SeriesSeasonalAdjustment.SeasonallyAdjusted)]
		[TestCase("SAAR", SeriesSeasonalAdjustment.SeasonallyAdjustedAnnualRate)]
		[TestCase("SSA", SeriesSeasonalAdjustment.SmoothedSeasonallyAdjusted)]
		[TestCase("NA", SeriesSeasonalAdjustment.NotApplicable)]
		[TestCase("SAAD", SeriesSeasonalAdjustment.Other)]
		[TestCase(null, SeriesSeasonalAdjustment.Unset)]
		public void ToSeasonalAdjustment_MapsFredsCode(string? code, SeriesSeasonalAdjustment expected)
		{
			Assert.That(FredSeriesSource.ToSeasonalAdjustment(code), Is.EqualTo(expected));
		}

		[TestCase("2013-07-31 09:26:16-05", "2013-07-31T14:26:16Z")]
		[TestCase("2026-03-08 01:30:00+05:30", "2026-03-07T20:00:00Z")]
		[TestCase("1970-01-01 00:00:00+00", null)]
		[TestCase("2013-07-31", null)]
		[TestCase(null, null)]
		public void ParseLastUpdated_ConvertsToUtc(string? lastUpdated, string? expectedUtc)
		{
			var expected = expectedUtc == null
				? (DtcTimestamp?)null
				: DtcTimestamp.FromDateTimeOffset(DateTimeOffset.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
			Assert.That(FredSeriesSource.ParseLastUpdated(lastUpdated), Is.EqualTo(expected));
		}

		[TestCase("GDPC1.xyz")]
		[TestCase("GDPC1.")]
		[TestCase(".pch")]
		[TestCase("GDP C1")]
		public void GetSeriesMetadataAsync_OfAnInvalidCode_IsAnUnknownSymbolWithoutCallingFred(string code)
		{
			var fred = new FakeFred(_ => FakeFredHandler.JsonResponse(Gdpc1Json));

			var ex = Assert.ThrowsAsync<SeriesSourceException>(() => fred.Source().GetSeriesMetadataAsync(code, CancellationToken.None));

			Assert.That(ex!.Reason, Is.EqualTo(SeriesRequestRejectReason.UnknownSymbol));
			Assert.That(fred.Requests, Is.Empty);
		}

		// FRED's errors (https://fred.stlouisfed.org/docs/api/fred/errors.html) as reject reasons and retry hints.
		[TestCase(HttpStatusCode.BadRequest, "Bad Request.  The series does not exist.", null, SeriesRequestRejectReason.UnknownSymbol, null)]
		[TestCase(HttpStatusCode.TooManyRequests, "Too Many Requests.  Exceeded Rate Limit", null, SeriesRequestRejectReason.SourceUnavailable, 60)]
		[TestCase(HttpStatusCode.TooManyRequests, "Too Many Requests.  Exceeded Rate Limit", 120, SeriesRequestRejectReason.SourceUnavailable, 120)]
		[TestCase(HttpStatusCode.InternalServerError, "Internal Server Error", null, SeriesRequestRejectReason.SourceUnavailable, null)]
		[TestCase(HttpStatusCode.ServiceUnavailable, "Service Unavailable", 30, SeriesRequestRejectReason.SourceUnavailable, 30)]
		[TestCase(HttpStatusCode.BadRequest, "Bad Request.  The value for variable api_key is not registered.", null, SeriesRequestRejectReason.SourceUnavailable, null)]
		[TestCase(HttpStatusCode.BadRequest, "Bad Request.  Variable realtime_start can not be before 1776-07-04.", null, SeriesRequestRejectReason.InvalidRequest, null)]
		public void FredErrors_MapToRejectReasons(HttpStatusCode status, string errorMessage, int? retryAfterHeader, SeriesRequestRejectReason reason, int? retryAfterSeconds)
		{
			var fred = new FakeFred(_ => ErrorResponse(status, errorMessage, retryAfterHeader));

			var metadataError = Assert.ThrowsAsync<SeriesSourceException>(() => fred.Source().GetSeriesMetadataAsync("GDPC1", CancellationToken.None));
			var vintagesError = Assert.ThrowsAsync<SeriesSourceException>(async () => await ReadAllAsync(fred.Source(), new SeriesVintagesRequest { Symbol = "GDPC1" }));

			foreach (var error in new[] { metadataError!, vintagesError! })
			{
				Assert.That(error.Reason, Is.EqualTo(reason));
				Assert.That(error.RetryAfter, Is.EqualTo(retryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null));
			}
			if (reason == SeriesRequestRejectReason.InvalidRequest)
				Assert.That(vintagesError!.Message, Does.Contain("realtime_start can not be before 1776-07-04"));
		}

		[Test]
		public void FredUnreachable_IsSourceUnavailable()
		{
			var fred = new FakeFred(_ => throw new HttpRequestException("No such host is known."));

			var ex = Assert.ThrowsAsync<SeriesSourceException>(() => fred.Source().GetSeriesMetadataAsync("GDPC1", CancellationToken.None));

			Assert.That(ex!.Reason, Is.EqualTo(SeriesRequestRejectReason.SourceUnavailable));
			Assert.That(ex.Message, Is.EqualTo("FRED cannot be reached."));
		}

		// A key-shaped run in FRED's message never reaches a reject's text. The run here is synthetic: 32 letters, not hex.
		[Test]
		public void Quote_MasksAnythingShapedLikeAnApiKey()
		{
			Assert.That(
				FredSeriesSource.Quote("Bad Request.  Key TESTKEYxTESTKEYxTESTKEYxTESTKEYx is wrong."),
				Is.EqualTo("Bad Request.  Key *** is wrong."));
		}

		// The series ID goes to FRED escaped, so a symbol cannot add query parameters.
		[Test]
		public void SeriesIdIsEscaped()
		{
			var fred = new FakeFred(_ => FakeFredHandler.JsonResponse("""{"seriess":[]}"""));

			Assert.ThrowsAsync<SeriesSourceException>(() => fred.Source().GetSeriesMetadataAsync("A&file_type=xml", CancellationToken.None));

			var query = Query(fred.Requests.Single());
			Assert.That(query["series_id"], Is.EqualTo("A&file_type=xml"));
			Assert.That(query.GetValues("file_type"), Is.EqualTo(new[] { "json" }));
		}

		// §5: output type 1; a null real-time bound is FRED's earliest or latest, sent explicitly because FRED's default is
		// today; a null observation bound is left to FRED's default; the suffix is FRED's units.
		[Test]
		public async Task GetSeriesVintagesAsync_WithoutBounds_AsksForAllRealTimeHistory()
		{
			var fred = new FakeFred(_ => FakeFredHandler.JsonResponse(Observations(0, Array.Empty<string>())));

			await ReadAllAsync(fred.Source(), new SeriesVintagesRequest { Symbol = "GDPC1.pch" });

			var query = Query(fred.Requests.Single());
			Assert.That(fred.Requests.Single().AbsolutePath, Does.EndWith("/series/observations"));
			Assert.That(query["series_id"], Is.EqualTo("GDPC1"));
			Assert.That(query["output_type"], Is.EqualTo("1"));
			Assert.That(query["realtime_start"], Is.EqualTo("1776-07-04"));
			Assert.That(query["realtime_end"], Is.EqualTo("9999-12-31"));
			Assert.That(query["observation_start"], Is.Null);
			Assert.That(query["observation_end"], Is.Null);
			Assert.That(query["units"], Is.EqualTo("pch"));
			Assert.That(query["sort_order"], Is.EqualTo("asc"));
			Assert.That(query["offset"], Is.EqualTo("0"));
			Assert.That(query["limit"], Is.EqualTo(FredSeriesSource.DefaultPageSize.ToString(CultureInfo.InvariantCulture)));
		}

		[Test]
		public async Task GetSeriesVintagesAsync_PassesItsBoundsToFred()
		{
			var fred = new FakeFred(_ => FakeFredHandler.JsonResponse(Observations(0, Array.Empty<string>())));

			await ReadAllAsync(fred.Source(), new SeriesVintagesRequest
			{
				Symbol = "GDPC1",
				ObservationStart = new DateOnly(1960, 1, 1),
				ObservationEnd = new DateOnly(1969, 10, 1),
				RealtimeStart = new DateOnly(2020, 3, 15),
				RealtimeEnd = new DateOnly(2020, 3, 15),
			});

			var query = Query(fred.Requests.Single());
			Assert.That(query["observation_start"], Is.EqualTo("1960-01-01"));
			Assert.That(query["observation_end"], Is.EqualTo("1969-10-01"));
			Assert.That(query["realtime_start"], Is.EqualTo("2020-03-15"));
			Assert.That(query["realtime_end"], Is.EqualTo("2020-03-15"));
			Assert.That(query["units"], Is.Null, "A symbol without a suffix asks for levels, FRED's default.");
		}

		// FRED's '.' is a row without a value; the rows keep FRED's real-time periods, clipped or not.
		[Test]
		public async Task GetSeriesVintagesAsync_MapsRowsAndMissingValues()
		{
			var fred = new FakeFred(_ => FakeFredHandler.JsonResponse(Observations(3, new[]
			{
				Row("2025-10-01", "2026-01-29", "2026-02-26", "30100.5"),
				Row("2025-10-01", "2026-02-27", "9999-12-31", "30120.25"),
				Row("2026-01-01", "2026-04-30", "9999-12-31", "."),
			})));

			var rows = await ReadAllAsync(fred.Source(), new SeriesVintagesRequest { Symbol = "GDPC1" });

			Assert.That(rows, Is.EqualTo(new[]
			{
				new SeriesVintage(new DateOnly(2025, 10, 1), new DateOnly(2026, 1, 29), new DateOnly(2026, 2, 26), 30100.5),
				new SeriesVintage(new DateOnly(2025, 10, 1), new DateOnly(2026, 2, 27), new DateOnly(9999, 12, 31), 30120.25),
				new SeriesVintage(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30), new DateOnly(9999, 12, 31), null),
			}));
		}

		// A page can end inside one observation's real-time periods, and FRED orders only by observation date: the rows of a
		// page's last date wait for the next page, and each batch is in real-time order.
		[Test]
		public async Task GetSeriesVintagesAsync_PagesThroughFred_InObservationAndRealTimeOrder()
		{
			var all = new[]
			{
				Row("2025-07-01", "2025-10-30", "9999-12-31", "1"),
				Row("2025-10-01", "2026-02-27", "9999-12-31", "3"),
				Row("2025-10-01", "2026-01-29", "2026-02-26", "2"),
				Row("2025-10-01", "2025-12-23", "2026-01-28", "."),
				Row("2026-01-01", "2026-04-30", "9999-12-31", "4"),
			};
			var fred = new FakeFred(request =>
			{
				var offset = int.Parse(Query(request)["offset"]!, CultureInfo.InvariantCulture);
				return FakeFredHandler.JsonResponse(Observations(all.Length, all.Skip(offset).Take(2).ToArray()));
			});
			var batches = new List<IReadOnlyList<SeriesVintage>>();
			var requestsBeforeBatch = new List<int>();

			await foreach (var batch in fred.Source(pageSize: 2).GetSeriesVintagesAsync(new SeriesVintagesRequest { Symbol = "GDPC1" }, CancellationToken.None))
			{
				requestsBeforeBatch.Add(fred.Requests.Count);
				batches.Add(batch);
			}

			Assert.That(fred.Requests.Select(r => Query(r)["offset"]), Is.EqualTo(new[] { "0", "2", "4" }));
			Assert.That(requestsBeforeBatch, Is.EqualTo(new[] { 1, 3 }), "A batch waited for more pages than it needs.");
			Assert.That(batches.SelectMany(b => b).Select(r => (r.ObservationDate, r.RealtimeStart)), Is.EqualTo(new[]
			{
				(new DateOnly(2025, 7, 1), new DateOnly(2025, 10, 30)),
				(new DateOnly(2025, 10, 1), new DateOnly(2025, 12, 23)),
				(new DateOnly(2025, 10, 1), new DateOnly(2026, 1, 29)),
				(new DateOnly(2025, 10, 1), new DateOnly(2026, 2, 27)),
				(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30)),
			}));
		}

		// A later page that goes back to an observation already returned cannot be put in order any more.
		[Test]
		public void GetSeriesVintagesAsync_OfPagesOutOfOrder_IsSourceUnavailable()
		{
			var pages = new[]
			{
				new[] { Row("2025-07-01", "2025-10-30", "9999-12-31", "1"), Row("2025-10-01", "2026-01-29", "9999-12-31", "2") },
				new[] { Row("2025-07-01", "2025-08-01", "2025-10-29", "0") },
			};
			var fred = new FakeFred(request =>
			{
				var offset = int.Parse(Query(request)["offset"]!, CultureInfo.InvariantCulture);
				return FakeFredHandler.JsonResponse(Observations(3, pages[offset / 2]));
			});

			var ex = Assert.ThrowsAsync<SeriesSourceException>(async () => await ReadAllAsync(fred.Source(pageSize: 2), new SeriesVintagesRequest { Symbol = "GDPC1" }));

			Assert.That(ex!.Reason, Is.EqualTo(SeriesRequestRejectReason.SourceUnavailable));
		}

		[TestCase("abc")]
		[TestCase("NaN")]
		[TestCase("")]
		public void GetSeriesVintagesAsync_OfAnUnreadableValue_IsSourceUnavailable(string value)
		{
			var fred = new FakeFred(_ => FakeFredHandler.JsonResponse(Observations(1, new[] { Row("2025-07-01", "2025-10-30", "9999-12-31", value) })));

			var ex = Assert.ThrowsAsync<SeriesSourceException>(async () => await ReadAllAsync(fred.Source(), new SeriesVintagesRequest { Symbol = "GDPC1" }));

			Assert.That(ex!.Reason, Is.EqualTo(SeriesRequestRejectReason.SourceUnavailable));
		}

		// StoresProvider routes "fred-" symbols to FRED without the prefix, as for security definitions.
		[TestCase("GDPC1")]
		[TestCase("fred-")]
		[TestCase("FRED-GDPC1")]
		public void StoresProvider_OfASymbolWithoutTheFredPrefix_IsAnUnknownSymbol(string symbol)
		{
			var fred = new FakeFred(_ => FakeFredHandler.JsonResponse(Gdpc1Json));
			using var provider = new StoresProvider(fred.Store, NullLoggerFactory.Instance);

			var metadataError = Assert.ThrowsAsync<SeriesSourceException>(() => provider.GetSeriesMetadataAsync(symbol, CancellationToken.None));
			var vintagesError = Assert.ThrowsAsync<SeriesSourceException>(async () => await ReadAllAsync(provider, new SeriesVintagesRequest { Symbol = symbol }));

			Assert.That(metadataError!.Reason, Is.EqualTo(SeriesRequestRejectReason.UnknownSymbol));
			Assert.That(vintagesError!.Reason, Is.EqualTo(SeriesRequestRejectReason.UnknownSymbol));
			Assert.That(fred.Requests, Is.Empty);
		}

		[Test]
		public async Task StoresProvider_RoutesFredSymbolsWithoutThePrefix()
		{
			var fred = new FakeFred(request => FakeFredHandler.JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/series", StringComparison.Ordinal)
				? Gdpc1Json
				: Observations(1, new[] { Row("2025-07-01", "2025-10-30", "9999-12-31", "1") })));
			using var provider = new StoresProvider(fred.Store, NullLoggerFactory.Instance);

			var metadata = await provider.GetSeriesMetadataAsync("fred-GDPC1.pc1", CancellationToken.None);
			var rows = await ReadAllAsync(provider, new SeriesVintagesRequest { Symbol = "fred-GDPC1.pc1" });

			Assert.That(metadata.UnitsTransformation, Is.EqualTo(SeriesUnitsTransformation.PercentChangeFromYearAgo));
			Assert.That(rows, Has.Count.EqualTo(1));
			Assert.That(fred.Requests.Select(r => Query(r)["series_id"]), Is.All.EqualTo("GDPC1"));
			Assert.That(Query(fred.Requests[1])["units"], Is.EqualTo("pc1"));
		}

		// A successful answer without its observations, or ending before FRED's own count, must not end in a terminator.
		[TestCase("""{"count":0}""")]
		[TestCase("""{"count":10,"observations":null}""")]
		public void GetSeriesVintagesAsync_OfAnAnswerWithoutObservations_IsSourceUnavailable(string json)
		{
			var fred = new FakeFred(_ => FakeFredHandler.JsonResponse(json));

			var ex = Assert.ThrowsAsync<SeriesSourceException>(async () => await ReadAllAsync(fred.Source(), new SeriesVintagesRequest { Symbol = "GDPC1" }));

			Assert.That(ex!.Reason, Is.EqualTo(SeriesRequestRejectReason.SourceUnavailable));
		}

		[Test]
		public void GetSeriesVintagesAsync_OfAShortPageBeforeFredsCount_IsSourceUnavailable()
		{
			var fred = new FakeFred(_ => FakeFredHandler.JsonResponse(Observations(5, new[] { Row("2025-07-01", "2025-10-30", "9999-12-31", "1") })));

			var ex = Assert.ThrowsAsync<SeriesSourceException>(async () => await ReadAllAsync(fred.Source(pageSize: 2), new SeriesVintagesRequest { Symbol = "GDPC1" }));

			Assert.That(ex!.Reason, Is.EqualTo(SeriesRequestRejectReason.SourceUnavailable));
		}

		// The provider's key ("test-key" here) never reaches an exception, and a NUL in FRED's text cannot block the reject.
		[Test]
		public void FredErrorText_WithTheApiKeyOrANul_IsCleanedAndTheRejectEncodes()
		{
			var fred = new FakeFred(_ => ErrorResponse(HttpStatusCode.BadRequest, "Bad Request.  Request with test-key\\u0000is wrong.", null));

			var ex = Assert.ThrowsAsync<SeriesSourceException>(async () => await ReadAllAsync(fred.Source(), new SeriesVintagesRequest { Symbol = "GDPC1" }));

			Assert.That(ex!.Reason, Is.EqualTo(SeriesRequestRejectReason.InvalidRequest));
			Assert.That(ex.ToString(), Does.Not.Contain("test-key"));
			// Ordinal: a culture-aware Contains ignores NUL and would always match.
			Assert.That(ex.Message.IndexOf('\0', StringComparison.Ordinal), Is.EqualTo(-1));
			var encoder = SomeDataProvider.DtcProtocolServer.DtcProtocol.MessageProtocol.CreateMessageProtocol(SomeDataProvider.DtcProtocolServer.DtcProtocol.MessageProtocol.PreferredEncoding).MessageEncoderFactory.CreateMessageEncoder();
			Assert.That(() => encoder.EncodeSeriesRequestReject(new SeriesRequestReject { RequestID = 1, RejectReason = ex.Reason, RejectText = ex.Message }), Throws.Nothing);
		}

		// FRED reports a missing series as an error, so a successful answer without a series is unusable, not a missing series.
		[TestCase("""{}""")]
		[TestCase("""{"seriess":null}""")]
		[TestCase("""{"seriess":[]}""")]
		[TestCase("""{"seriess":[null]}""")]
		public void GetSeriesMetadataAsync_OfASuccessfulAnswerWithoutASeries_IsSourceUnavailable(string json)
		{
			var fred = new FakeFred(_ => FakeFredHandler.JsonResponse(json));

			var ex = Assert.ThrowsAsync<SeriesSourceException>(() => fred.Source().GetSeriesMetadataAsync("GDPC1", CancellationToken.None));

			Assert.That(ex!.Reason, Is.EqualTo(SeriesRequestRejectReason.SourceUnavailable));
			Assert.That(ex.InnerException, Is.InstanceOf<FredApiException>().With.Property(nameof(FredApiException.StatusCode)).EqualTo(HttpStatusCode.OK));
		}

		// FRED's own answer that the series does not exist stays an unknown symbol, with FRED's documented HTTP 400 or a 404.
		[TestCase(HttpStatusCode.BadRequest)]
		[TestCase(HttpStatusCode.NotFound)]
		public void GetSeriesMetadataAsync_OfFredsSeriesDoesNotExist_IsAnUnknownSymbol(HttpStatusCode status)
		{
			var fred = new FakeFred(_ => ErrorResponse(status, "Bad Request.  The series does not exist.", null));

			var ex = Assert.ThrowsAsync<SeriesSourceException>(() => fred.Source().GetSeriesMetadataAsync("GDPC1", CancellationToken.None));

			Assert.That(ex!.Reason, Is.EqualTo(SeriesRequestRejectReason.UnknownSymbol));
		}

		// An HTTP 200 body that cannot be read fails with a message quoting FRED's text, with the provider's key ("test-key")
		// masked; no exception in the chain keeps the unmasked text, because a log writes the whole chain.
		[Test]
		public void FredAnswerThatCannotBeRead_KeepsTheApiKeyOutOfTheWholeExceptionChain()
		{
			var fred = new FakeFred(request => FakeFredHandler.JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/series", StringComparison.Ordinal)
				? """{"seriess":"test-key"}"""
				: Observations(1, new[] { Row("2025-07-01", "2025-10-30", "9999-12-31", "test-key") })));

			var metadataError = Assert.ThrowsAsync<SeriesSourceException>(() => fred.Source().GetSeriesMetadataAsync("GDPC1", CancellationToken.None));
			var vintagesError = Assert.ThrowsAsync<SeriesSourceException>(async () => await ReadAllAsync(fred.Source(), new SeriesVintagesRequest { Symbol = "GDPC1" }));

			foreach (var error in new[] { metadataError!, vintagesError! })
			{
				Assert.That(error.Reason, Is.EqualTo(SeriesRequestRejectReason.SourceUnavailable));
				var fredError = error.InnerException as FredApiException;
				Assert.That(fredError, Is.Not.Null);
				Assert.That(fredError!.StatusCode, Is.EqualTo(HttpStatusCode.OK));
				Assert.That(fredError.Message, Does.Contain("cannot be read").And.Contain("***"));
				for (Exception? e = error; e != null; e = e.InnerException)
					Assert.That(e.Message, Does.Not.Contain("test-key"));
				Assert.That(error.ToString(), Does.Not.Contain("test-key"));
			}
		}

		static async Task<List<SeriesVintage>> ReadAllAsync(ISeriesSource source, SeriesVintagesRequest request)
		{
			var rows = new List<SeriesVintage>();
			await foreach (var batch in source.GetSeriesVintagesAsync(request, CancellationToken.None))
				rows.AddRange(batch);
			return rows;
		}

		static System.Collections.Specialized.NameValueCollection Query(Uri uri) => HttpUtility.ParseQueryString(uri.Query);

		static System.Collections.Specialized.NameValueCollection Query(HttpRequestMessage request) => Query(request.RequestUri!);

		static string Row(string date, string realtimeStart, string realtimeEnd, string value) =>
			$$"""{"realtime_start":"{{realtimeStart}}","realtime_end":"{{realtimeEnd}}","date":"{{date}}","value":"{{value}}"}""";

		static string Observations(int count, IReadOnlyList<string> rows) =>
			$$"""{"realtime_start":"1776-07-04","realtime_end":"9999-12-31","output_type":1,"order_by":"observation_date","sort_order":"asc","count":{{count}},"observations":[{{string.Join(",", rows)}}]}""";

		static HttpResponseMessage ErrorResponse(HttpStatusCode status, string errorMessage, int? retryAfterSeconds)
		{
			var body = $$"""{"error_code":{{(int)status}},"error_message":"{{errorMessage}}"}""";
			var response = FakeFredHandler.JsonResponse(body, status);
			if (retryAfterSeconds is { } seconds)
				response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
			return response;
		}

		/// <summary>FRED behind a fake handler, recording the requests' URIs.</summary>
		sealed class FakeFred
		{
			readonly List<Uri> _requests = new();

			public FakeFred(Func<HttpRequestMessage, HttpResponseMessage> respond)
			{
				var handler = new FakeFredHandler(request =>
				{
					lock (_requests)
						_requests.Add(request.RequestUri!);
					return respond(request);
				});
				var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://fred.test/fred/") };
				Store = new Store(new Service(httpClient, (ServiceApiKey)"test-key", NullLoggerFactory.Instance), NullLoggerFactory.Instance);
			}

			public Store Store { get; }

			public IReadOnlyList<Uri> Requests
			{
				get
				{
					lock (_requests)
						return _requests.ToArray();
				}
			}

			public FredSeriesSource Source(int pageSize = FredSeriesSource.DefaultPageSize) => new(Store, pageSize);
		}
	}
}
