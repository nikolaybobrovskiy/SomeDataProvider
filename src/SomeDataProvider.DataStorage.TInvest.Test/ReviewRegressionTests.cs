namespace SomeDataProvider.DataStorage.TInvest.Test;

using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class ReviewRegressionTests
{
	[Test]
	public async Task UnsupportedCandidateIsSkippedAndNegativeCached()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent("BAD") with { Increment = 0 });
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent("GOOD"));
		using var store = new TInvestStore(gateway, new TInvestOptions());
		Assert.That((await store.SearchSymbolsAsync("text", SymbolSearchType.ByDescription, 50)).Select(row => row.Code), Is.EqualTo(new[] { "GOOD" }));
		var count = gateway.Lookups.Count;
		Assert.That((await store.SearchSymbolsAsync("text", SymbolSearchType.ByDescription, 50)).Count, Is.EqualTo(1));
		Assert.That(gateway.Lookups.Count, Is.EqualTo(count));
		var reject = Assert.ThrowsAsync<TInvestRequestException>(async () => await store.GetSymbolAsync(TInvestStore.LookupCode("BAD", "TESTCC")));
		Assert.That(reject!.Kind, Is.EqualTo(TInvestFailureKind.Unsupported));
	}

	[TestCase(TInvestFailureKind.Source, false)]
	[TestCase(TInvestFailureKind.Limit, false)]
	[TestCase(TInvestFailureKind.Source, true)]
	[TestCase(TInvestFailureKind.Limit, true)]
	public async Task SourceFailuresRejectAndOnlyCompleteLimitPrefixesSurvive(TInvestFailureKind kind, bool firstComplete)
	{
		using var gateway = new ScriptedTInvestGateway();
		if (firstComplete) gateway.Instruments.Add(ScriptedTInvestGateway.Invent("GOOD"));
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent("FAIL"));
		gateway.Resolve = (ticker, _, _) => ticker == "FAIL" ? throw new TInvestRequestException("Safe scripted failure.", kind) : Task.FromResult<TInvestInstrument?>(gateway.Instruments[0]);
		using var store = new TInvestStore(gateway, new TInvestOptions());
		if (firstComplete && kind == TInvestFailureKind.Limit)
			Assert.That((await store.SearchSymbolsAsync("text", SymbolSearchType.ByDescription, 50)).Count, Is.EqualTo(1));
		else
			Assert.ThrowsAsync<TInvestRequestException>(async () => await store.SearchSymbolsAsync("text", SymbolSearchType.ByDescription, 50));
	}

	[Test]
	public async Task SlowLookupDoesNotBlockCachedOrUnrelatedColdSymbolsAndSameKeyIsDeduplicated()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent("CACHED"));
		using var store = new TInvestStore(gateway, new TInvestOptions(), clock);
		await store.GetSymbolAsync(TInvestStore.LookupCode("CACHED", "TESTCC"));
		var release = new TaskCompletionSource<TInvestInstrument?>(TaskCreationOptions.RunContinuationsAsynchronously);
		gateway.Resolve = (ticker, _, ct) => ticker == "SLOW" ? release.Task.WaitAsync(ct) : Task.FromResult<TInvestInstrument?>(ScriptedTInvestGateway.Invent(ticker));
		var first = store.GetSymbolAsync(TInvestStore.LookupCode("SLOW", "TESTCC")).AsTask();
		var joined = store.GetSymbolAsync(TInvestStore.LookupCode("SLOW", "TESTCC")).AsTask();
		Assert.That(await store.GetSymbolAsync(TInvestStore.LookupCode("CACHED", "TESTCC")), Is.Not.Null);
		Assert.That(await store.GetSymbolAsync(TInvestStore.LookupCode("OTHER", "TESTCC")), Is.Not.Null);
		release.SetResult(ScriptedTInvestGateway.Invent("SLOW"));
		await Task.WhenAll(first, joined).WaitAsync(TimeSpan.FromSeconds(5));
		Assert.That(gateway.Lookups.Count(pair => pair.Ticker == "SLOW"), Is.EqualTo(1));
	}

	[TestCase("credentialManager")]
	[TestCase("environment")]
	public void TokenSourceIsCaseSensitive(string source) => Assert.Throws<ArgumentException>(() => new TInvestOptions { TokenSource = source }.Validate());

	[Test]
	public void EnvironmentIsCaseSensitive() => Assert.Throws<ArgumentException>(() => new TInvestOptions { Environment = "sandbox" }.Validate());

	[TestCase("Production")]
	[TestCase("Sandbox")]
	public void DefaultCredentialTargetUsesEnvironmentAndWhitespaceFileTokenIsRejected(string environment)
	{
		var observed = string.Empty;
		var source = new TInvestTokenSource(target =>
		{
			observed = target;
			return "invented-secret";
		});
		var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["StoresOptions:TInvest:Token"] = "  " }).Build();
		source.Load(new TInvestOptions { TokenSource = "CredentialManager", Environment = environment }, config);
		Assert.That(observed, Is.EqualTo("SomeDataProvider/TInvest/" + environment));
		source.Load(new TInvestOptions { TokenSource = "CredentialManager", CredentialTarget = "invented-target" }, config);
		Assert.That(observed, Is.EqualTo("invented-target"));
		Assert.Throws<InvalidOperationException>(() => source.Load(new TInvestOptions { TokenSource = "File" }, config));
	}

	[Test]
	public void UnsupportedExpiryCannotReachWireEncoder() => Assert.That(TInvestStore.Complete(ScriptedTInvestGateway.Invent() with { Expiration = new DateTimeOffset(2107, 1, 1, 0, 0, 0, TimeSpan.Zero) }), Is.False);
}
