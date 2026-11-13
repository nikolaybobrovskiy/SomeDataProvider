namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Linq;
using System.Net.Http;
using Dtc.Protocol.Private;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SomeDataProvider.DataStorage.Fred;
using SomeDataProvider.DtcProtocolServer.Data;

public partial class ProviderInformationWireTests
{
	[TestCase(true)]
	[TestCase(false)]
	public void FredCatalogExpansionReturnsChildrenThenOnlyTheRequestedOffsetPage(bool knownTotal)
	{
		var calls = new System.Collections.Generic.List<Uri>();
		using var http = new HttpClient(new FakeFredHandler(r =>
		{
			calls.Add(r.RequestUri!);
			return FakeFredHandler.JsonResponse(r.RequestUri!.AbsolutePath.EndsWith("children", StringComparison.Ordinal)
				? "{\"categories\":[{\"id\":17,\"name\":\"Invented category\",\"parent_id\":0}]}"
				: "{" + (knownTotal ? "\"count\":5," : string.Empty) + "\"offset\":0,\"limit\":2,\"seriess\":[{\"id\":\"SYNTHA\",\"title\":\"Invented A\"},{\"id\":\"SYNTHB\",\"title\":\"Invented B\"}]}");
		})) { BaseAddress = new Uri("https://synthetic.invalid/fred/") };
		using var service = new Service(http, (ServiceApiKey)new string('a', 32), NullLoggerFactory.Instance);
		using var store = new Store(service, NullLoggerFactory.Instance);
		using var host = new TInvestTestHost(new StoresProvider(store, NullLoggerFactory.Instance));
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-fred" });
		Read(client);
		var request = new SymbolCatalogRequest { Catalog = "fred", NodeID = "fred", PageSize = 2 };
		var (children, childEnd) = Page(client, request);
		Assert.That(((SymbolCatalogNode)children.Single()).NodeID, Is.EqualTo("fred/17"));
		Assert.That(childEnd.NextCursor, Is.Not.Null);
		Assert.That(calls.Count, Is.EqualTo(1));
		var (rows, end) = Page(client, request with { Cursor = childEnd.NextCursor, SnapshotVersion = childEnd.SnapshotVersion });
		Assert.That(rows.Cast<SymbolCatalogEntry>().Select(r => r.Symbol), Is.EqualTo(new[] { "fred-SYNTHA", "fred-SYNTHB" }));
		Assert.That(rows.Cast<SymbolCatalogEntry>().All(r => r.Features == null && r.UIDEpoch == null && r.Exchange == string.Empty), Is.True);
		Assert.That(end.Consistency, Is.EqualTo(2));
		Assert.That(end.Total, Is.Null, "The raw series count is not a distinct combined category/series total.");
		Assert.That(end.NextCursor, Is.Not.Null);
		Assert.That(calls.Count, Is.EqualTo(2));
		Assert.That(calls[1].Query, Does.Contain("offset=0").And.Contain("limit=2").And.Contain("order_by=series_id").And.Contain("sort_order=asc"));
		Page(client, request with { Cursor = childEnd.NextCursor });
		Assert.That(calls.Count, Is.EqualTo(2), "Revisiting a bounded page uses its local view cache.");
	}

	[Test]
	public void FredCatalogRejectsBeforeHelloAndAReadFailureLeavesTheConnectionUsable()
	{
		var calls = 0;
		using var http = new HttpClient(new FakeFredHandler(_ =>
		{
			calls++;
			return FakeFredHandler.JsonResponse("{\"error_message\":\"Synthetic outage\"}", System.Net.HttpStatusCode.ServiceUnavailable);
		})) { BaseAddress = new Uri("https://synthetic.invalid/fred/") };
		using var service = new Service(http, (ServiceApiKey)new string('a', 32), NullLoggerFactory.Instance);
		using var store = new Store(service, NullLoggerFactory.Instance);
		using var host = new TInvestTestHost(new StoresProvider(store, NullLoggerFactory.Instance));
		using var client = Connect(host);
		Send(client, new SymbolCatalogRequest { Catalog = "fred", NodeID = "fred" });
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("FeatureUnavailable"));
		Assert.That(calls, Is.Zero);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-fred" });
		Read(client);
		Send(client, new SymbolCatalogRequest { Catalog = "fred", NodeID = "fred" });
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("SourceUnavailable"));
		Assert.That(calls, Is.EqualTo(1));
		var (_, end) = Page(client, new SymbolCatalogRequest { Catalog = "fred" });
		Assert.That(end.RowCount, Is.EqualTo(1));
		Assert.That(calls, Is.EqualTo(1));
	}

	[Test]
	public void FredCatalogRootIsNegotiatedWithoutTouchingEitherSource()
	{
		var calls = 0;
		using var http = new HttpClient(new FakeFredHandler(_ =>
		{
			calls++;
			throw new InvalidOperationException("The root must be local.");
		})) { BaseAddress = new Uri("https://synthetic.invalid/fred/") };
		using var service = new Service(http, (ServiceApiKey)new string('a', 32), NullLoggerFactory.Instance);
		using var store = new Store(service, NullLoggerFactory.Instance);
		using var host = new TInvestTestHost(new StoresProvider(store, NullLoggerFactory.Instance));
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-fred" });
		Assert.That(((ProviderHelloResponse)Read(client)).Features & ProviderInformationFeatures.SymbolCatalogV1, Is.Not.Zero);
		var (rows, end) = Page(client, new SymbolCatalogRequest { Catalog = "fred" });
		var root = (SymbolCatalogNode)rows.Single();
		Assert.That(root.NodeID, Is.EqualTo("fred"));
		Assert.That(root.Name, Is.EqualTo("FRED"));
		Assert.That(root.ParentNodeID, Is.Null);
		Assert.That(end.Consistency, Is.EqualTo(2));
		Assert.That(end.Total, Is.EqualTo(1));
		Assert.That(calls, Is.Zero);
	}
}
