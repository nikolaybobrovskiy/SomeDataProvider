namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Data;

[TestFixture]
public class TInvestProviderRegressionTests
{
	[TestCase(false)]
	[TestCase(true)]
	public void ProductionStartupIsDeferredUntilLoggerIsAttachedAndFailureKeepsFred(bool readOnly)
	{
		using var logs = new RecordingLoggerFactory();
		using var gateway = new ScriptedTInvestGateway { Access = new(true, new[] { readOnly }) };
		var created = 0;
		using var provider = new StoresProvider(new StoresProvider.StoresOptions { TInvest = new TInvestOptions { TokenSource = "Environment" } }, logs, new TInvestToken("invented-startup-secret"), () =>
		{
			created++;
			return gateway;
		});
		Assert.That(created, Is.Zero, "Constructor injection must not make upstream calls before logging.");
		Assert.That(logs.Entries, Is.Empty);
		provider.Initialize();
		provider.Initialize();
		provider.StartupTask.GetAwaiter().GetResult();
		Assert.That(created, Is.EqualTo(1));
		Assert.That(provider.SourcesDescription, Is.EqualTo(readOnly ? "FRED, T-Invest (production)" : "FRED"));
		var messages = string.Join("\n", logs.Entries.Select(entry => entry.Message));
		Assert.That(messages, Does.Contain(readOnly ? "startup check passed" : "read-only T-Invest token is required"));
		Assert.That(messages.Contains("invented-startup-secret", StringComparison.Ordinal), Is.False);
	}

	[Test]
	public async Task MixedSearchKeepsSourceOrderCapAndFredOnlyRouting()
	{
		var fredCalls = 0;
		var handler = new FakeFredHandler(_ =>
		{
			fredCalls++;
			return FakeFredHandler.JsonResponse("{\"seriess\":[{\"id\":\"SYNMAC\",\"title\":\"Invented macro series\",\"frequency_short\":\"M\",\"units_short\":\"Index\",\"seasonal_adjustment_short\":\"NSA\",\"popularity\":10}]}");
		});
		var http = new HttpClient(handler) { BaseAddress = new Uri("https://fred.test/fred/") };
		var fred = new SomeDataProvider.DataStorage.Fred.Store(new SomeDataProvider.DataStorage.Fred.Service(http, (SomeDataProvider.DataStorage.Fred.ServiceApiKey)"invented-key", NullLoggerFactory.Instance), NullLoggerFactory.Instance);
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.AddRange(Enumerable.Range(0, 49).Select(i => ScriptedTInvestGateway.Invent("SYNTH" + i)));
		using var store = new TInvestStore(gateway, new TInvestOptions());
		using var provider = new StoresProvider(fred, store, NullLoggerFactory.Instance);
		var cold = await provider.SearchSymbolsAsync("query", SymbolSearchType.ByDescription, 50);
		Assert.That(cold.Count, Is.EqualTo(21));
		Assert.That(cold.Take(20).All(row => row.DataService == DataService.TInvest), Is.True);
		Assert.That(cold.Last().Code, Is.EqualTo("fred-SYNMAC"));
		foreach (var instrument in gateway.Instruments) await store.GetSymbolAsync(TInvestStore.LookupCode(instrument.Short.Ticker, instrument.Short.ClassCode));
		var warm = await provider.SearchSymbolsAsync("query", SymbolSearchType.ByDescription, 50);
		Assert.That(warm.Count, Is.EqualTo(50));
		Assert.That(warm.Last().DataService, Is.EqualTo(DataService.Fred));
		var searches = gateway.Searches;
		Assert.That((await provider.SearchSymbolsAsync("fred-SYNMAC", SymbolSearchType.BySymbol, 50)).Single().Code, Is.EqualTo("fred-SYNMAC"));
		Assert.That(gateway.Searches, Is.EqualTo(searches));
		var calls = fredCalls;
		await provider.SearchSymbolsAsync("query", SymbolSearchType.ByDescription, 50, "TESTCC", SymbolType.Unset);
		await provider.SearchSymbolsAsync("query", SymbolSearchType.ByDescription, 50, string.Empty, SymbolType.Stock);
		Assert.That(fredCalls, Is.EqualTo(calls));
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent("FAILED"));
		gateway.Resolve = (_, _, _) => throw new TInvestRequestException("Safe source failure.");
		var partial = await provider.SearchWithStatusAsync("FAILED", SymbolSearchType.BySymbol, 50, string.Empty, SymbolType.Unset, default);
		Assert.That(partial.Symbols.Single().Code, Is.EqualTo("fred-SYNMAC"));
		Assert.That(partial.Failures.Single().Source, Is.EqualTo("t-invest"));
		Assert.That(partial.HasSuccessfulSource, Is.True);
		Assert.That(fredCalls, Is.EqualTo(calls + 1), "The healthy FRED source still runs after T-Invest fails.");
	}
}
