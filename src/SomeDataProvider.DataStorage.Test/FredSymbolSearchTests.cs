// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Test
{
	using System;
	using System.Linq;
	using System.Net;
	using System.Net.Http;
	using System.Threading.Tasks;

	using Microsoft.Extensions.Logging.Abstractions;

	using NUnit.Framework;

	using SomeDataProvider.DataStorage.Fred;

	/// <summary>
	/// SomeDataProvider.DataStorage.Fred.Service.SearchSeriesAsync (PLAN.md §7.3 item 7), against recorded FRED
	/// responses: no test here calls FRED live.
	/// </summary>
	[TestFixture]
	public class FredSymbolSearchTests
	{
		[Test]
		public async Task SearchSeriesAsync_ReturnsMatches_InFredsOrder()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(@"{
				""seriess"": [
					{""id"":""GDP"",""title"":""Gross Domestic Product"",""frequency_short"":""Q"",""units_short"":""Bil. of $"",""seasonal_adjustment_short"":""SAAR"",""popularity"":95},
					{""id"":""GDPC1"",""title"":""Real Gross Domestic Product"",""frequency_short"":""Q"",""units_short"":""Bil. of Chn. 2017$"",""seasonal_adjustment_short"":""SAAR"",""popularity"":90}
				]
			}"));
			using var svc = NewService(handler);

			var results = await svc.SearchSeriesAsync("gross domestic product", Service.SearchTypeFullText, 10);

			Assert.That(results.Select(r => r.Id.ToString()), Is.EqualTo(new[] { "GDP", "GDPC1" }));
			Assert.That(handler.LastRequestUri!.Query, Does.Contain($"search_text={Uri.EscapeDataString("gross domestic product")}"));
			Assert.That(handler.LastRequestUri!.Query, Does.Contain("search_type=full_text"));
			Assert.That(handler.LastRequestUri!.Query, Does.Contain("limit=10"));
		}

		[Test]
		public async Task SearchSeriesAsync_WithNoMatches_ReturnsEmpty()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(@"{""seriess"":[]}"));
			using var svc = NewService(handler);

			var results = await svc.SearchSeriesAsync("no such series", Service.SearchTypeFullText, 10);

			Assert.That(results, Is.Empty);
		}

		// SECURITY_DEFINITION_REJECT (the caller's answer for this) is reserved for a real failure, as opposed to a
		// well-formed search that simply found nothing (the test above).
		[Test]
		public void SearchSeriesAsync_OnFredApiError_Throws()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(
				@"{""error_code"":400,""error_message"":""Bad Request. The value for variable search_text is not a valid string.""}",
				HttpStatusCode.BadRequest));
			using var svc = NewService(handler);

			Assert.That(async () => await svc.SearchSeriesAsync("x", Service.SearchTypeFullText, 10), Throws.Exception);
		}

		// A non-success HTTP status without FRED's own error_message field (e.g. an upstream outage) must still be
		// a hard failure, not an empty "seriess" result: GetJsonAsync never checks the HTTP status itself.
		[Test]
		public void SearchSeriesAsync_OnNonSuccessStatusWithoutAnErrorMessageField_Throws()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(
				@"{""message"":""upstream unavailable""}", HttpStatusCode.ServiceUnavailable));
			using var svc = NewService(handler);

			Assert.That(async () => await svc.SearchSeriesAsync("x", Service.SearchTypeFullText, 10), Throws.Exception);
		}

		static Service NewService(FakeFredHandler handler)
		{
			var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://fred.test/fred/") };
			return new Service(httpClient, (ServiceApiKey)"test-key", NullLoggerFactory.Instance);
		}
	}
}
