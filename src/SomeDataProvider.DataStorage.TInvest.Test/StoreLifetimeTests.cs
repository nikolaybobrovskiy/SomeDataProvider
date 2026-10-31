namespace SomeDataProvider.DataStorage.TInvest.Test;

using NUnit.Framework;
using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class StoreLifetimeTests
{
	[Test]
	public async Task DailyTimerRefreshesIdentityWithoutClientLookupAndDisposalStopsTimer()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var store = new TInvestStore(gateway, new TInvestOptions(), clock);
		await store.GetSymbolAsync(TInvestStore.LookupCode("SYNTH", "TESTCC"));
		gateway.Instruments[0] = gateway.Instruments[0] with { Short = gateway.Instruments[0].Short with { Uid = "invented-reused-ticker" } };
		clock.Advance(TimeSpan.FromHours(3));
		await store.RefreshTask.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.That(gateway.Lookups.Count, Is.EqualTo(2));
		var result = (TInvestSymbol)(await store.GetSymbolAsync(TInvestStore.LookupCode("SYNTH", "TESTCC")))!;
		Assert.That(result.Instrument.Short.Uid, Is.EqualTo("invented-reused-ticker"));
		Assert.That(gateway.Lookups.Count, Is.EqualTo(2));
		store.Dispose();
		Assert.That(clock.ActiveTimers, Is.Zero);
	}

	[Test]
	public async Task SearchSharedGateIsInsideItsEightSecondBudget()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		gateway.Resolve = async (_, _, ct) =>
		{
			await Task.Delay(TimeSpan.FromSeconds(60), clock, ct);
			return null;
		};
		using var store = new TInvestStore(gateway, new TInvestOptions(), clock);
		var holding = store.ResolveAsync("SYNTH", "TESTCC", clock.GetUtcNow().AddSeconds(20));
		var search = store.SearchSymbolsAsync("SYNTH", SymbolSearchType.BySymbol, 50).AsTask();
		clock.Advance(TimeSpan.FromSeconds(8));
		Assert.ThrowsAsync<TInvestRequestException>(async () => await search.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.That(holding.IsCompleted, Is.False);
		clock.Advance(TimeSpan.FromSeconds(12));
		Assert.ThrowsAsync<TInvestRequestException>(async () => await holding.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Test]
	public async Task ZeroSearchCapDoesNotCallUpstreamAndExactTickerPrecedesPrefixes()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent("SYNTHETIC"));
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent("SYNTH"));
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent("OTHER"));
		using var store = new TInvestStore(gateway, new TInvestOptions());
		Assert.That(await store.SearchSymbolsAsync("SYNTH", SymbolSearchType.BySymbol, 0), Is.Empty);
		Assert.That(gateway.Searches, Is.Zero);
		var result = await store.SearchSymbolsAsync("SYNTH", SymbolSearchType.BySymbol, 50);
		Assert.That(result.Select(row => row.Code), Is.EqualTo(new[] { "SYNTH", "SYNTHETIC" }));
	}

	[Test]
	public async Task FailedDailyRefreshNeverReturnsExpiredDefinition()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var store = new TInvestStore(gateway, new TInvestOptions(), clock);
		await store.GetSymbolAsync(TInvestStore.LookupCode("SYNTH", "TESTCC"));
		gateway.Resolve = (_, _, _) => throw new TInvestRequestException("Scripted failure.");
		clock.Advance(TimeSpan.FromHours(3));
		await store.RefreshTask;
		Assert.ThrowsAsync<TInvestRequestException>(async () => await store.GetSymbolAsync(TInvestStore.LookupCode("SYNTH", "TESTCC")));
	}
}
