namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Dtc.Protocol.Private;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;

public partial class ProviderInformationWireTests
{
	[Test]
	public void CatalogFiltersTheWholeBoardAndDeduplicatesPaths()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.AddRange(Enumerable.Range(0, 450).Select(i => ScriptedTInvestGateway.Invent("SYNTH" + i.ToString("D3")) with { Board = "TESTCC" }));
		gateway.Instruments.Add(gateway.Instruments[449]);
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-catalog" });
		Read(client);
		var request = new SymbolCatalogRequest { Catalog = "t-invest", NodeID = "t-bank/shares/TESTCC", Filter = "synth449" };
		var (rows, end) = Page(client, request);
		Assert.That(rows.Count, Is.EqualTo(1));
		Assert.That(end.Total, Is.EqualTo(1));
		Assert.That(((SymbolCatalogEntry)rows[0]).Symbol, Is.EqualTo("SYNTH449-TESTCC"));
	}

	[TestCase("filter")]
	[TestCase("node")]
	[TestCase("size")]
	[TestCase("snapshot")]
	[TestCase("tamper")]
	public void CatalogRejectsChangedCursorBindings(string change)
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.AddRange(Enumerable.Range(0, 3).Select(i => ScriptedTInvestGateway.Invent("SYNTH" + i) with { Board = "TESTCC" }));
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-catalog" });
		Read(client);
		var request = new SymbolCatalogRequest { Catalog = "t-invest", NodeID = "t-bank/shares/TESTCC", PageSize = 1 };
		var (_, end) = Page(client, request);
		request = request with { Cursor = end.NextCursor, SnapshotVersion = end.SnapshotVersion };
		request = change switch
		{
			"filter" => request with { Filter = "changed" }, "node" => request with { NodeID = "t-bank" },
			"size" => request with { PageSize = 2 }, "snapshot" => request with { SnapshotVersion = "changed" },
			_ => request with { Cursor = "invented-cursor" },
		};
		Send(client, request);
		var result = Read(client);
		Assert.That(result, Is.TypeOf<ProviderRequestReject>());
		Assert.That(((ProviderRequestReject)result).ReasonCode, Is.EqualTo(change == "snapshot" ? "SnapshotExpired" : "InvalidCursor"));
		Assert.That(gateway.CatalogCalls.Count, Is.EqualTo(5));
	}

	[Test]
	public void IncompleteCatalogMetadataDoesNotPretendToKnowContextHints()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent() with { Board = "TESTCC", Increment = 0 });
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-catalog" });
		Read(client);
		var (rows, _) = Page(client, new SymbolCatalogRequest { Catalog = "t-invest", NodeID = "t-bank/shares/TESTCC" });
		var row = (SymbolCatalogEntry)rows.Single();
		Assert.That(row.Features, Is.Null);
		Assert.That(row.UIDEpoch, Is.Null);
	}

	[TestCase(5000, 200)]
	[TestCase(5000, 500)]
	[TestCase(20000, 200)]
	[TestCase(20000, 500)]
	public void CatalogPagesEveryLeafWithoutDefinitionRequests(int count, int size)
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.AddRange(Enumerable.Range(0, count).Select(i => ScriptedTInvestGateway.Invent("SYNTH" + i.ToString("D5")) with { Board = "TESTCC" }));
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-catalog" });
		Read(client);
		var request = new SymbolCatalogRequest { RequestID = 80, Catalog = "t-invest", NodeID = "t-bank/shares/TESTCC", PageSize = size };
		var symbols = new HashSet<string>();
		var pages = 0;
		do
		{
			var (rows, end) = Page(client, request);
			Assert.That(rows.Count, Is.EqualTo(size));
			Assert.That(end.Total, Is.EqualTo(count));
			Assert.That(end.RowCount, Is.EqualTo(rows.Count));
			Assert.That(end.Consistency, Is.EqualTo(1));
			for (var i = 0; i < rows.Count; i++)
			{
				var row = (SymbolCatalogEntry)rows[i];
				Assert.That(row.RowIndex, Is.EqualTo(i));
				Assert.That(row.RowsInPage, Is.EqualTo(rows.Count));
				Assert.That(row.SnapshotVersion, Is.EqualTo(end.SnapshotVersion));
				Assert.That(symbols.Add(row.Symbol), Is.True);
			}
			request = request with { Cursor = end.NextCursor, SnapshotVersion = end.SnapshotVersion };
			pages++;
		}
		while (request.Cursor != null && pages <= count / size);
		Assert.That(symbols.Count, Is.EqualTo(count));
		Assert.That(pages, Is.EqualTo(count / size));
		Assert.That(gateway.Lookups, Is.Empty);
		Assert.That(gateway.CatalogCalls.Count, Is.EqualTo(5));
	}

	[TestCase(TInvestInstrumentKind.Share, "shares", 1)]
	[TestCase(TInvestInstrumentKind.Bond, "bonds", 2)]
	[TestCase(TInvestInstrumentKind.Etf, "etfs", 3)]
	[TestCase(TInvestInstrumentKind.Future, "futures", 4)]
	[TestCase(TInvestInstrumentKind.Currency, "currencies", 5)]
	public void CatalogBoardContainsTheBulkInstrumentWithContextHints(TInvestInstrumentKind kind, string kindNode, int wireKind)
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent(kind: kind) with { Board = "TESTCC", PriceUnit = "currency" });
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-catalog" });
		Read(client);
		Send(client, new SymbolCatalogRequest { Catalog = "t-invest", NodeID = "t-bank/" + kindNode + "/TESTCC" });
		var message = Read(client);
		Assert.That(message, Is.TypeOf<SymbolCatalogEntry>());
		var row = (SymbolCatalogEntry)message;
		var end = (SymbolCatalogEnd)Read(client);
		Assert.That(row.Symbol, Is.EqualTo("SYNTH-TESTCC"));
		Assert.That(row.Exchange, Is.Empty);
		Assert.That(row.Board, Is.EqualTo("TESTCC"));
		Assert.That(row.Kind, Is.EqualTo(wireKind));
		Assert.That(row.Features, Is.EqualTo(111UL));
		Assert.That(row.UIDEpoch, Is.Not.Null);
		Assert.That(end.Total, Is.EqualTo(1));
		Assert.That(gateway.Lookups, Is.Empty, "Browse must not hydrate definitions.");
		Send(client, new InstrumentContextRequest { Symbol = row.Symbol });
		var context = (InstrumentContextResponse)Read(client);
		Assert.That(row.Features, Is.EqualTo(context.Features));
		Assert.That(row.UIDEpoch, Is.EqualTo(context.UIDEpoch));
	}

	[Test]
	public void CatalogStartsWithTheTBankRootInsteadOfAReject()
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-catalog" });
		Read(client);
		Send(client, new SymbolCatalogRequest { RequestID = 71, Catalog = "t-invest" });
		var message = Read(client);
		Assert.That(message, Is.TypeOf<SymbolCatalogNode>());
		var root = (SymbolCatalogNode)message;
		Assert.That(root.Name, Is.EqualTo("T-Bank"));
		Assert.That(root.ParentNodeID, Is.Null);
		var end = (SymbolCatalogEnd)Read(client);
		Assert.That(end.Total, Is.EqualTo(1));
		Assert.That(end.Consistency, Is.EqualTo(1));
	}

	static (List<ProviderInformationMessage> Rows, SymbolCatalogEnd End) Page(TcpClient client, SymbolCatalogRequest request)
	{
		Send(client, request);
		var rows = new List<ProviderInformationMessage>();
		while (true)
		{
			var row = Read(client);
			if (row is SymbolCatalogEnd end) return (rows, end);
			Assert.That(row, Is.Not.TypeOf<ProviderRequestReject>());
			rows.Add(row);
		}
	}
}
