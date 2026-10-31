namespace SomeDataProvider.DataStorage.TInvest.Test;

using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class StoreTests
{
	[Test]
	public async Task ColdSearchIsBoundedAndEveryRowIsCompleteThenWarmSearchReturnsFifty()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.AddRange(Enumerable.Range(0, 60).Select(i => ScriptedTInvestGateway.Invent("SYNTH" + i)));
		using var store = new TInvestStore(gateway, new TInvestOptions());
		var cold = await store.SearchAsync("SYNTH", SymbolSearchType.BySymbol, 50, string.Empty, SymbolType.Unset);
		Assert.That(cold.Count, Is.EqualTo(20));
		Assert.That(cold.All(i => i.MinPriceIncrement > 0 && i.Currency == "ZZZ" && i.Description!.Contains("lot 10", StringComparison.Ordinal)), Is.True);
		Assert.That(gateway.Lookups.Count, Is.EqualTo(20));
		await store.SearchSymbolsAsync("SYNTH", SymbolSearchType.BySymbol, 50);
		var warm = await store.SearchSymbolsAsync("SYNTH", SymbolSearchType.BySymbol, 50);
		Assert.That(warm.Count, Is.EqualTo(50));
		var calls = gateway.Lookups.Count;
		var symbol = await store.GetSymbolAsync(TInvestStore.LookupCode(warm.First().Code, warm.First().Exchange!));
		Assert.That(symbol, Is.SameAs(warm.First()));
		Assert.That(gateway.Lookups.Count, Is.EqualTo(calls));
		Assert.That(gateway.Searches, Is.EqualTo(3));
	}

	[TestCase(TInvestInstrumentKind.Share, SymbolType.Stock, 0.01, 1)]
	[TestCase(TInvestInstrumentKind.Etf, SymbolType.Stock, 0.01, 1)]
	[TestCase(TInvestInstrumentKind.Bond, SymbolType.Bond, 0.1, 1)]
	[TestCase(TInvestInstrumentKind.Future, SymbolType.Futures, 0.25, 5)]
	[TestCase(TInvestInstrumentKind.Currency, SymbolType.Forex, 0.01, 1)]
	[TestCase(TInvestInstrumentKind.StockOption, SymbolType.StockOption, 0.01, 1)]
	[TestCase(TInvestInstrumentKind.FuturesOption, SymbolType.FuturesOption, 0.01, 1)]
	[TestCase(TInvestInstrumentKind.IndexOption, SymbolType.IndexOption, 0.01, 1)]
	public async Task Definitions(TInvestInstrumentKind kind, SymbolType type, double value, double size)
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent(kind: kind));
		using var store = new TInvestStore(gateway, new TInvestOptions());
		var symbol = (TInvestSymbol)(await store.GetSymbolAsync(TInvestStore.LookupCode("SYNTH", "TESTCC")))!;
		Assert.That(symbol.Type, Is.EqualTo(type));
		Assert.That(symbol.CurrencyValuePerIncrement, Is.EqualTo(value));
		Assert.That(symbol.ContractSize, Is.EqualTo(size));
		Assert.That(symbol.ExchangeSymbol, Is.EqualTo("SYNTH_TESTCC"));
	}

	[TestCase("0.000000001", 9)]
	[TestCase("0.010000000", 2)]
	[TestCase("1", 0)]
	[TestCase("2.5", 1)]
	public void DisplayDecimals(string increment, int expected)
	{
		var instrument = ScriptedTInvestGateway.Invent() with { Increment = decimal.Parse(increment, System.Globalization.CultureInfo.InvariantCulture) };
		Assert.That(new TInvestSymbol(instrument).NumberOfDecimals, Is.EqualTo(expected));
	}

	[Test]
	public async Task FilteringDropsUnsupportedEmptyClassAndNonTradeableAndHonorsKind()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent("VALID"));
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent("OTHER", "OTHERCC"));
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent("UNSUPPORTED", kind: TInvestInstrumentKind.Unsupported));
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent("EMPTY", string.Empty));
		var unavailable = ScriptedTInvestGateway.Invent("NOTRADE");
		gateway.Instruments.Add(unavailable with { Short = unavailable.Short with { ApiTradeAvailable = false } });
		using var store = new TInvestStore(gateway, new TInvestOptions());
		var result = await store.SearchAsync("anything", SymbolSearchType.ByDescription, 50, "TESTCC", SymbolType.Stock);
		Assert.That(result.Select(s => s.Code), Is.EqualTo(new[] { "VALID" }));
		Assert.That(await store.SearchAsync("anything", SymbolSearchType.ByDescription, 50, "TESTCC", SymbolType.Bond), Is.Empty);
	}

	[Test]
	public async Task NegativeCacheAndDailyRefreshUseClock()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		using var store = new TInvestStore(gateway, new TInvestOptions(), clock);
		var key = TInvestStore.LookupCode("SYNTH", "TESTCC");
		Assert.That(await store.GetSymbolAsync(key), Is.Null);
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		Assert.That(await store.GetSymbolAsync(key), Is.Null);
		clock.Advance(TimeSpan.FromMinutes(10));
		var before = (TInvestSymbol)(await store.GetSymbolAsync(key))!;
		gateway.Instruments[0] = gateway.Instruments[0] with { Short = before.Instrument.Short with { Uid = "invented-replacement" } };
		clock.Advance(TimeSpan.FromHours(3));
		var after = (TInvestSymbol)(await store.GetSymbolAsync(key))!;
		Assert.That(after.Instrument.Short.Uid, Is.EqualTo("invented-replacement"));
		Assert.That(gateway.Lookups.Count, Is.EqualTo(3));
		Assert.That(clock.ActiveTimers, Is.EqualTo(1), "Only the daily refresh timer remains.");
	}

	[Test]
	public async Task SearchStopsAtEightSecondsWithoutSendingIncompleteDefinitions()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.AddRange(Enumerable.Range(0, 50).Select(i => ScriptedTInvestGateway.Invent("SYNTH" + i)));
		gateway.Resolve = async (_, _, ct) =>
		{
			await Task.Delay(TimeSpan.FromSeconds(10), clock, ct);
			return null;
		};
		using var store = new TInvestStore(gateway, new TInvestOptions(), clock);
		var result = store.SearchSymbolsAsync("SYNTH", SymbolSearchType.BySymbol, 50).AsTask();
		clock.Advance(TimeSpan.FromSeconds(8));
		Assert.ThrowsAsync<TInvestRequestException>(async () => await result.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.That(clock.ActiveTimers, Is.EqualTo(1), "Only the daily refresh timer remains.");
	}
}

[TestFixture]
public class TokenTests
{
	[TestCase("CredentialManager", "credential")]
	[TestCase("Environment", "environment")]
	[TestCase("File", "file")]
	public void ExactlyOneSource(string source, string expected)
	{
		var reads = new List<string>();
		var loader = new TInvestTokenSource(_ =>
		{
			reads.Add("credential");
			return "credential";
		}, _ =>
		{
			reads.Add("environment");
			return "environment";
		});
		var token = loader.Load(new TInvestOptions { TokenSource = source }, Config("file"));
		Assert.That(token!.Redact(expected), Is.EqualTo("<redacted>"));
		Assert.That(reads.Count, Is.EqualTo(source == "File" ? 0 : 1));
		Assert.That(token.ToString(), Is.EqualTo("<redacted>"));
	}

	[TestCase("CredentialManager")]
	[TestCase("Environment")]
	[TestCase("File")]
	public void MissingSourceDoesNotFallBack(string source)
	{
		var loader = new TInvestTokenSource(_ => null, _ => null);
		Assert.Throws<InvalidOperationException>(() => loader.Load(new TInvestOptions { TokenSource = source }, Config(null)));
	}

	[TestCase(false, 1, true, true)]
	[TestCase(false, 0, true, false)]
	[TestCase(false, 2, false, false)]
	[TestCase(true, 0, false, true)]
	public void EnforcesProductionReadOnly(bool sandbox, int accounts, bool readOnly, bool accepted)
	{
		using var gateway = new ScriptedTInvestGateway { Access = new(true, Enumerable.Repeat(readOnly, accounts).ToArray()) };
		var options = new TInvestOptions { Environment = sandbox ? "Sandbox" : "Production" };
		if (accepted) Assert.DoesNotThrowAsync(() => TInvestStartup.CheckAsync(gateway, options, DateTimeOffset.UtcNow.AddSeconds(8)));
		else Assert.ThrowsAsync<TInvestRequestException>(() => TInvestStartup.CheckAsync(gateway, options, DateTimeOffset.UtcNow.AddSeconds(8)));
	}

	[TestCase("")]
	[TestCase("CredentialManager")]
	[TestCase("Environment")]
	[TestCase("File")]
	public void TokenSourceRange(string source) => Assert.DoesNotThrow(() => new TInvestOptions { TokenSource = source }.Validate());

	[Test]
	public void RejectsInvalidTokenSource() => Assert.Throws<ArgumentException>(() => new TInvestOptions { TokenSource = "fallback" }.Validate());

	[TestCase("Production")]
	[TestCase("Sandbox")]
	public void EnvironmentRange(string environment) => Assert.DoesNotThrow(() => new TInvestOptions { Environment = environment }.Validate());

	[Test]
	public void RejectsInvalidEnvironment() => Assert.Throws<ArgumentException>(() => new TInvestOptions { Environment = "Other" }.Validate());

	[TestCase(1)]
	[TestCase(256)]
	public void CredentialTargetRange(int length) => Assert.DoesNotThrow(() => new TInvestOptions { CredentialTarget = new string('x', length) }.Validate());

	[TestCase("")]
	[TestCase(" ")]
	[TestCase("bad\nvalue")]
	public void RejectsInvalidTarget(string value) => Assert.Throws<ArgumentException>(() => new TInvestOptions { CredentialTarget = value }.Validate());

	[Test]
	public void RejectsOverlongTarget() => Assert.Throws<ArgumentException>(() => new TInvestOptions { CredentialTarget = new string('x', 257) }.Validate());

	[TestCase(false)]
	[TestCase(true)]
	public void BooleanRangeAndCombinedEnds(bool flag)
	{
		foreach (var environment in new[] { "Production", "Sandbox" })
		{
			foreach (var length in new[] { 1, 256 })
				Assert.DoesNotThrow(() => new TInvestOptions { SearchOnlyApiTradeable = flag, Environment = environment, CredentialTarget = new string('x', length) }.Validate());
		}
	}

	[Test]
	public void RejectsInvalidBooleanBinding()
	{
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SearchOnlyApiTradeable"] = "sometimes" }).Build();
		Assert.Throws<InvalidOperationException>(() => configuration.Get<TInvestOptions>());
	}

	static IConfiguration Config(string? token) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["StoresOptions:TInvest:Token"] = token }).Build();
}
