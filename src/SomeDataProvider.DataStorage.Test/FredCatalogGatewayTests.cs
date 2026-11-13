namespace SomeDataProvider.DataStorage.Test;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SomeDataProvider.DataStorage.Fred;

[TestFixture]
public class FredCatalogGatewayTests
{
	[Test]
	public async Task AdapterReadsOnlyExplicitCategoryCallsAndPreservesAnUnknownTotal()
	{
		var calls = 0;
		using var handler = new FakeFredHandler(r =>
		{
			calls++;
			if (r.RequestUri!.AbsolutePath.EndsWith("children", StringComparison.Ordinal)) return FakeFredHandler.JsonResponse("{\"categories\":[{\"id\":29,\"parent_id\":17,\"name\":\"Invented category\"}]}");
			return FakeFredHandler.JsonResponse("{\"offset\":200,\"limit\":200,\"seriess\":[{\"id\":\"SYNTHSERIES\",\"title\":\"Invented series\"}]}");
		});
		using var http = Client(handler);
		using var service = Service(http);
		var gateway = (IFredCatalogGateway)service;
		var children = await gateway.GetChildrenAsync(17, default);
		Assert.That(children.Single(), Is.EqualTo(new FredCatalogCategory(29, 17, "Invented category")));
		var page = await gateway.GetSeriesPageAsync(17, 200, 200, default);
		Assert.That(page.Total, Is.Null);
		Assert.That(page.Rows.Single().Id, Is.EqualTo("SYNTHSERIES"));
		Assert.That(calls, Is.EqualTo(2));
		Assert.That(handler.LastRequestUri!.Query.Contains("category_id=17&offset=200&limit=200&order_by=series_id&sort_order=asc", StringComparison.Ordinal), Is.True);
	}

	[TestCase("{}")]
	[TestCase("{\"count\":-1,\"offset\":0,\"limit\":2,\"seriess\":[]}")]
	[TestCase("{\"count\":\"unknown\",\"offset\":0,\"limit\":2,\"seriess\":[]}")]
	[TestCase("{\"offset\":1,\"limit\":2,\"seriess\":[]}")]
	[TestCase("{\"offset\":0,\"limit\":3,\"seriess\":[]}")]
	[TestCase("{\"count\":0,\"offset\":0,\"limit\":2,\"seriess\":[{\"id\":\"SYNTH\",\"title\":\"Invented\"}]}")]
	[TestCase("{\"offset\":0,\"limit\":2,\"seriess\":[{\"id\":\"SYNTH\"}]}")]
	public void MalformedPagesAreNotEmptySuccessfulListings(string json)
	{
		using var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse(json));
		using var http = Client(handler);
		using var service = Service(http);
		Assert.That(async () => await ((IFredCatalogGateway)service).GetSeriesPageAsync(17, 0, 2, default), Throws.TypeOf<FredCatalogException>());
	}

	[TestCase(0)]
	[TestCase(501)]
	public void InvalidPageLimitsMakeNoHttpCall(int limit)
	{
		var calls = 0;
		using var handler = new FakeFredHandler(_ =>
		{
			calls++;
			return FakeFredHandler.JsonResponse("{}");
		});
		using var http = Client(handler);
		using var service = Service(http);
		Assert.That(async () => await ((IFredCatalogGateway)service).GetSeriesPageAsync(17, 0, limit, default), Throws.TypeOf<ArgumentOutOfRangeException>());
		Assert.That(calls, Is.Zero);
	}

	[TestCase(HttpStatusCode.TooManyRequests)]
	[TestCase(HttpStatusCode.InternalServerError)]
	[TestCase(HttpStatusCode.OK)]
	public void SourceErrorsNeverExposeAnEchoedCredential(HttpStatusCode status)
	{
		var syntheticKey = new string('a', 32);
		using var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse("{\"error_message\":\"" + syntheticKey + "\"}", status));
		using var http = Client(handler);
		using var service = Service(http);
		var exception = Assert.ThrowsAsync<FredCatalogException>(async () => await ((IFredCatalogGateway)service).GetChildrenAsync(17, default))!;
		Assert.That(exception.ToString().Contains(syntheticKey, StringComparison.Ordinal), Is.False);
		Assert.That(exception.InnerException, Is.Null);
	}

	[Test]
	public void EchoedCredentialInACatalogRowIsRejectedBeforePublication()
	{
		var syntheticKey = new string('a', 32);
		using var handler = new FakeFredHandler(_ => FakeFredHandler.JsonResponse("{\"categories\":[{\"id\":29,\"parent_id\":17,\"name\":\"" + syntheticKey + "\"}]}"));
		using var http = Client(handler);
		using var service = Service(http);
		Assert.That(async () => await ((IFredCatalogGateway)service).GetChildrenAsync(17, default), Throws.TypeOf<FredCatalogException>());
	}

	[Test]
	public void TransportExceptionsCannotRetainCredentialBearingUrls()
	{
		var syntheticKey = new string('a', 32);
		using var handler = new FakeFredHandler(_ => throw new HttpRequestException("Synthetic request api_key=" + syntheticKey));
		using var http = Client(handler);
		using var service = Service(http);
		var exception = Assert.ThrowsAsync<FredCatalogException>(async () => await ((IFredCatalogGateway)service).GetChildrenAsync(17, default))!;
		Assert.That(exception.ToString().Contains(syntheticKey, StringComparison.Ordinal), Is.False);
		Assert.That(exception.InnerException, Is.Null);
	}

	[Test]
	public void AnOversizedBodyIsRejectedWithoutParsingOrAnotherCall()
	{
		var calls = 0;
		using var handler = new FakeFredHandler(_ =>
		{
			calls++;
			return FakeFredHandler.JsonResponse(new string(' ', SomeDataProvider.DataStorage.Fred.Service.MaxCatalogResponseBytes + 1));
		});
		using var http = Client(handler);
		using var service = Service(http);
		Assert.That(async () => await ((IFredCatalogGateway)service).GetChildrenAsync(17, default), Throws.TypeOf<FredCatalogException>());
		Assert.That(calls, Is.EqualTo(1));
	}

	[Test]
	public void CallerCancellationIsNotTurnedIntoAnEmptyOrUnavailableListing()
	{
		using var cancel = new CancellationTokenSource();
		cancel.Cancel();
		using var handler = new FakeFredHandler(_ => throw new InvalidOperationException("Canceled before HTTP dispatch."));
		using var http = Client(handler);
		using var service = Service(http);
		Assert.That(async () => await ((IFredCatalogGateway)service).GetChildrenAsync(17, cancel.Token), Throws.InstanceOf<OperationCanceledException>());
	}

	static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://synthetic.invalid/fred/") };
	static Service Service(HttpClient http) => new(http, (ServiceApiKey)new string('a', 32), NullLoggerFactory.Instance);
}
