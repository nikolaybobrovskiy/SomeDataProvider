namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Linq;
using System.Threading.Tasks;
using Dtc.Protocol.Private;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;

public partial class ProviderInformationWireTests
{
	[Test]
	public void RefreshedCatalogChangesHintsAndInvalidatesTheOldCursor()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.AddRange(new[] { ScriptedTInvestGateway.Invent(), ScriptedTInvestGateway.Invent("SYNTHTWO") }.Select(i => i with { Board = "TESTCC" }));
		using var host = new TInvestTestHost(gateway, clock);
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-catalog" });
		Read(client);
		var request = new SymbolCatalogRequest { Catalog = "t-invest", NodeID = "t-bank/shares/TESTCC", PageSize = 1 };
		var (oldRows, oldEnd) = Page(client, request);
		var old = (SymbolCatalogEntry)oldRows.Single();
		gateway.Instruments[0] = gateway.Instruments[0] with { Increment = 0.05m };
		clock.Advance(TimeSpan.FromHours(1));
		var (newRows, newEnd) = Page(client, request);
		Assert.That(newEnd.SnapshotVersion, Is.Not.EqualTo(oldEnd.SnapshotVersion));
		Assert.That(((SymbolCatalogEntry)newRows.Single()).UIDEpoch, Is.Not.EqualTo(old.UIDEpoch));
		Send(client, request with { Cursor = oldEnd.NextCursor, SnapshotVersion = oldEnd.SnapshotVersion });
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("SnapshotExpired"));
		Send(client, request with { Cursor = oldEnd.NextCursor });
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("SnapshotExpired"));
		Assert.That(oldRows.Single(), Is.EqualTo(old), "Already returned rows remain immutable.");
	}

	[Test]
	public void CatalogHierarchyPreservesDistinctBoardsAndDefaultsToTwoHundredRows()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.AddRange(Enumerable.Range(0, 501).Select(i => ScriptedTInvestGateway.Invent("SYNTH", "TEST" + i.ToString("D3")) with { Board = "TEST" + i.ToString("D3") }));
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-catalog" });
		Assert.That(((ProviderHelloResponse)Read(client)).Features & ProviderInformationFeatures.SymbolCatalogV1, Is.Not.Zero);
		var (kinds, _) = Page(client, new SymbolCatalogRequest { Catalog = "t-invest", NodeID = "t-bank" });
		Assert.That(kinds.Cast<SymbolCatalogNode>().Select(n => n.Name), Is.EqualTo(new[] { "Shares", "Bonds", "ETFs", "Futures", "Currencies" }));
		var request = new SymbolCatalogRequest { Catalog = "t-invest", NodeID = "t-bank/shares" };
		var (first, end) = Page(client, request);
		Assert.That(first.Count, Is.EqualTo(200));
		Assert.That(end.Total, Is.EqualTo(501));
		Assert.That(first.Cast<SymbolCatalogNode>().All(n => n.ParentNodeID == "t-bank/shares"), Is.True);
		var (second, secondEnd) = Page(client, request with { Cursor = end.NextCursor });
		var (third, thirdEnd) = Page(client, request with { Cursor = secondEnd.NextCursor });
		Assert.That(third.Count, Is.EqualTo(101));
		Assert.That(thirdEnd.NextCursor, Is.Null);
		Assert.That(first.Concat(second).Concat(third).Cast<SymbolCatalogNode>().Select(n => n.NodeID).Distinct().Count(), Is.EqualTo(501));
	}

	[Test]
	public async Task AnOlderClientNeverTriggersBulkListsOrReceivesUnsolicitedCatalogFrames()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var host = new TInvestTestHost(gateway, clock);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		Assert.That((await client.GetSecurityDefinitionAsync("SYNTH", "TESTCC"))!.Symbol, Is.EqualTo("SYNTH"));
		clock.Advance(TimeSpan.FromHours(2));
		Assert.That(gateway.CatalogCalls, Is.Empty);
	}

	[Test]
	public void CatalogNotAdvertisedAtHelloRemainsUnavailableUntilReconnect()
	{
		using var gateway = new ScriptedTInvestGateway { SupportsCatalog = false };
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-catalog" });
		Assert.That(((ProviderHelloResponse)Read(client)).Features & ProviderInformationFeatures.SymbolCatalogV1, Is.Zero);
		gateway.SupportsCatalog = true;
		Send(client, new SymbolCatalogRequest { Catalog = "t-invest" });
		var response = Read(client);
		Assert.That(response, Is.TypeOf<ProviderRequestReject>());
		Assert.That(((ProviderRequestReject)response).ReasonCode, Is.EqualTo("FeatureUnavailable"));
		Assert.That(gateway.CatalogCalls, Is.Empty);
	}

	[Test]
	public void CatalogRequiresNegotiationAndNeverRoutesFredIntoTheTBankGateway()
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new SymbolCatalogRequest { Catalog = "t-invest" });
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("FeatureUnavailable"));
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-catalog" });
		Read(client);
		Send(client, new SymbolCatalogRequest { Catalog = "fred", Cursor = "invented-cross-source-cursor" });
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("FeatureUnavailable"));
		Assert.That(gateway.CatalogCalls, Is.Empty);
	}
}
