namespace SomeDataProvider.DtcProtocolServer.Test;

using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Data;

[TestFixture]
public class TInvestRoutingTests
{
	[TestCase("SYNTH", "TESTCC", true)]
	[TestCase("SYNTH-TESTCC", "", true)]
	[TestCase("SYNTH", "", false)]
	[TestCase("SYNTH-UNKNOWN", "", false)]
	[TestCase("fredish-TESTCC", "", false)]
	public async Task RoutesWithoutGuessingTickerOnly(string code, string exchange, bool found)
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var store = new TInvestStore(gateway, new TInvestOptions());
		using var provider = new StoresProvider(null, store, NullLoggerFactory.Instance);
		var route = await provider.GetSymbolsStoreAsync(code, exchange);
		Assert.That(route.HasValue, Is.EqualTo(found));
		if (found)
		{
			var symbol = await route!.Value.SymbolsStore.GetSymbolAsync(route.Value.SymbolCode);
			Assert.That(symbol?.Code, Is.EqualTo("SYNTH"));
		}
	}

	[Test]
	public async Task ColdCombinedLookupLearnsClassAndNegativeLookupsAreCached()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var store = new TInvestStore(gateway, new TInvestOptions());
		using var provider = new StoresProvider(null, store, NullLoggerFactory.Instance);
		Assert.That(await provider.GetSymbolsStoreAsync("SYNTH-TESTCC", string.Empty), Is.Not.Null);
		Assert.That(await provider.GetSymbolsStoreAsync("UNKNOWN-NOCLASS", string.Empty), Is.Null);
		Assert.That(await provider.GetSymbolsStoreAsync("UNKNOWN-NOCLASS", string.Empty), Is.Null);
		Assert.That(gateway.Lookups.Count, Is.EqualTo(2));
	}

	[TestCase("fred-X", "X")]
	[TestCase("fred-X.pch", "X.pch")]
	public async Task FredUnchanged(string code, string local)
	{
		var handler = new FakeFredHandler(_ => throw new System.InvalidOperationException("Routing must not send an HTTP request."));
		var http = new System.Net.Http.HttpClient(handler) { BaseAddress = new System.Uri("https://fred.test/fred/") };
		var service = new SomeDataProvider.DataStorage.Fred.Service(http, (SomeDataProvider.DataStorage.Fred.ServiceApiKey)"test-key", NullLoggerFactory.Instance);
		var fred = new SomeDataProvider.DataStorage.Fred.Store(service, NullLoggerFactory.Instance);
		using var provider = new StoresProvider(fred, NullLoggerFactory.Instance);
		var route = await provider.GetSymbolsStoreAsync(code, string.Empty);
		Assert.That(route?.SymbolCode, Is.EqualTo(local));
		Assert.That(route?.SymbolsStore, Is.SameAs(fred));
		Assert.That((await provider.GetSymbolsStoreAsync(code, "BAD"))?.SymbolsStore, Is.SameAs(fred));
	}
}
