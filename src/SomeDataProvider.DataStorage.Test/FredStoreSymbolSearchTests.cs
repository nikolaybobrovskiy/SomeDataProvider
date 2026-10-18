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

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DataStorage.Fred;

	/// <summary>
	/// SomeDataProvider.DataStorage.Fred.Store.SearchSymbolsAsync (PLAN.md §7.3 item 7), against recorded FRED
	/// responses: no test here calls FRED live.
	/// </summary>
	[TestFixture]
	public class FredStoreSymbolSearchTests
	{
		const string OneSeriesJson = @"{""seriess"":[{""id"":""GDP"",""title"":""Gross Domestic Product"",""frequency_short"":""Q"",""units_short"":""Bil. of $"",""seasonal_adjustment_short"":""SAAR"",""popularity"":95}]}";

		[Test]
		public async Task SearchSymbolsAsync_WithEmptySearchText_ReturnsEmptyWithoutCallingFred()
		{
			var handler = new FakeFredHandler(_ => throw new InvalidOperationException("FRED must not be called for an empty SearchText."));
			using var store = NewStore(handler);

			var results = await store.SearchSymbolsAsync(string.Empty, SymbolSearchType.ByDescription, 10);

			Assert.That(results, Is.Empty);
		}

		[Test]
		public async Task SearchSymbolsAsync_BySymbol_SendsSeriesIdSearchType()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(OneSeriesJson));
			using var store = NewStore(handler);

			await store.SearchSymbolsAsync("GDP", SymbolSearchType.BySymbol, 10);

			Assert.That(handler.LastRequestUri!.Query, Does.Contain("search_type=series_id"));
		}

		[Test]
		public async Task SearchSymbolsAsync_ByDescription_SendsFullTextSearchType()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(OneSeriesJson));
			using var store = NewStore(handler);

			await store.SearchSymbolsAsync("gross domestic product", SymbolSearchType.ByDescription, 10);

			Assert.That(handler.LastRequestUri!.Query, Does.Contain("search_type=full_text"));
		}

		// A units suffix such as ".pch" is this provider's own convention (see GetFredSymbol), not part of FRED's
		// series ID, so it is stripped for a by-symbol search. An unrecognized "."-suffix is left alone.
		[TestCase("GDP.pc1", "GDP")]
		[TestCase("GDP", "GDP")]
		[TestCase("U.S.GDP", "U.S.GDP")]
		public async Task SearchSymbolsAsync_BySymbol_StripsARecognizedUnitsSuffix(string searchText, string expectedFredSearchText)
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(OneSeriesJson));
			using var store = NewStore(handler);

			await store.SearchSymbolsAsync(searchText, SymbolSearchType.BySymbol, 10);

			Assert.That(handler.LastRequestUri!.Query, Does.Contain($"search_text={Uri.EscapeDataString(expectedFredSearchText)}"));
		}

		[Test]
		public async Task SearchSymbolsAsync_ByDescription_KeepsDotsInTheSearchText()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(OneSeriesJson));
			using var store = NewStore(handler);

			await store.SearchSymbolsAsync("U.S. GDP", SymbolSearchType.ByDescription, 10);

			Assert.That(handler.LastRequestUri!.Query, Does.Contain($"search_text={Uri.EscapeDataString("U.S. GDP")}"));
		}

		[Test]
		public async Task SearchSymbolsAsync_ForwardsTheDocumentedCapAsFredsLimit()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(@"{""seriess"":[]}"));
			using var store = NewStore(handler);

			await store.SearchSymbolsAsync("GDP", SymbolSearchType.ByDescription, 37);

			Assert.That(handler.LastRequestUri!.Query, Does.Contain("limit=37"));
		}

		// Same Symbol form and Description format GetSymbolAsync gives (StoresProvider adds the "fred-" prefix
		// above this layer), so a search result and a definition agree.
		[Test]
		public async Task SearchSymbolsAsync_BuildsTheSameSymbolAndDescriptionFormatAsGetSymbolAsync()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(OneSeriesJson));
			using var store = NewStore(handler);

			var results = await store.SearchSymbolsAsync("GDP", SymbolSearchType.BySymbol, 10);

			var symbol = results.Single();
			Assert.That(symbol.Code, Is.EqualTo("GDP"));
			Assert.That(symbol.Description, Is.EqualTo("Gross Domestic Product (Bil. of $/Q/SAAR)"));
		}

		[Test]
		public void SearchSymbolsAsync_OnFredApiError_Throws()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(
				@"{""error_code"":400,""error_message"":""Bad Request.""}", HttpStatusCode.BadRequest));
			using var store = NewStore(handler);

			Assert.That(async () => await store.SearchSymbolsAsync("GDP", SymbolSearchType.ByDescription, 10), Throws.Exception);
		}

		static Store NewStore(HttpMessageHandler handler)
		{
			var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://fred.test/fred/") };
			var service = new Service(httpClient, (ServiceApiKey)"test-key", NullLoggerFactory.Instance);
			return new Store(service, NullLoggerFactory.Instance);
		}
	}
}
