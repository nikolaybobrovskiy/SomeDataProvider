// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
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
	using SomeDataProvider.DtcProtocolServer.Data;

	/// <summary>
	/// StoresProvider.SearchSymbolsAsync (PLAN.md §7.3 item 7): the "fred-" routing prefix it adds to and strips from
	/// symbol codes elsewhere (GetSymbolsStoreAsync, GetKnownSymbolsAsync) also applies to a search's SearchText and
	/// results. No test here calls FRED: the FredStore it wraps is built on a fake HttpMessageHandler, through
	/// StoresProvider's internal test constructor.
	/// </summary>
	[TestFixture]
	public class StoresProviderSymbolSearchTests
	{
		const string OneSeriesJson = @"{""seriess"":[{""id"":""GDP"",""title"":""Gross Domestic Product"",""frequency_short"":""Q"",""units_short"":""Bil. of $"",""seasonal_adjustment_short"":""SAAR"",""popularity"":95}]}";

		[Test]
		public async Task SearchSymbolsAsync_WrapsResultsWithTheFredPrefix()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(OneSeriesJson));
			using var provider = NewProvider(handler);

			var results = await provider.SearchSymbolsAsync("GDP", SymbolSearchType.BySymbol, 10);

			Assert.That(results.Single().Code, Is.EqualTo("fred-GDP"));
		}

		// A leading "fred-" in SearchText is this provider's own routing prefix, not part of FRED's series ID or
		// title; a client that pastes a "fred-"-prefixed symbol code into the search box still gets a match.
		[Test]
		public async Task SearchSymbolsAsync_StripsALeadingFredRoutingPrefixFromSearchText()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(OneSeriesJson));
			using var provider = NewProvider(handler);

			await provider.SearchSymbolsAsync("fred-GDP", SymbolSearchType.BySymbol, 10);

			Assert.That(handler.LastRequestUri!.Query, Does.Contain($"search_text={Uri.EscapeDataString("GDP")}"));
		}

		[Test]
		public async Task SearchSymbolsAsync_WithoutALeadingFredPrefix_SendsTheSearchTextUnchanged()
		{
			var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(OneSeriesJson));
			using var provider = NewProvider(handler);

			await provider.SearchSymbolsAsync("GDP", SymbolSearchType.BySymbol, 10);

			Assert.That(handler.LastRequestUri!.Query, Does.Contain($"search_text={Uri.EscapeDataString("GDP")}"));
		}

		static StoresProvider NewProvider(HttpMessageHandler handler)
		{
			var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://fred.test/fred/") };
			var service = new Service(httpClient, (ServiceApiKey)"test-key", NullLoggerFactory.Instance);
			var store = new Store(service, NullLoggerFactory.Instance);
			return new StoresProvider(store, NullLoggerFactory.Instance);
		}
	}
}
