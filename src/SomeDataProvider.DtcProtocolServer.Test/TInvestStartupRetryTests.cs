namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Data;

[TestFixture]
public class TInvestStartupRetryTests
{
	[Test]
	public async Task TransientFailuresAreRetriedEveryThirtySecondsAndTheSourceStartsOnce()
	{
		var clock = new ManualTimeProvider();
		using var logs = new RecordingLoggerFactory();
		var gateways = new List<ScriptedTInvestGateway>();
		using var provider = Create(clock, logs, gateways, new TInvestRequestException("T-Invest source request failed."), new TimeoutException());
		var started = 0;
		provider.TInvestStarted += () => started++;
		provider.Initialize();
		await WaitForTimer(clock);
		Assert.That(gateways, Has.Count.EqualTo(1));
		Assert.That(provider.SourcesDescription, Is.EqualTo("FRED"));

		var starting = Assert.ThrowsAsync<TInvestRequestException>(async () => await provider.GetSymbolsStoreAsync("SYNTH-TESTCC", string.Empty));
		Assert.That(starting!.Kind, Is.EqualTo(TInvestFailureKind.Starting), "Not an unknown symbol.");
		Assert.That(starting.Message, Does.Contain("retry in 30 s"));

		clock.Advance(TimeSpan.FromSeconds(29));
		Assert.That(gateways, Has.Count.EqualTo(1));
		clock.Advance(TimeSpan.FromSeconds(1));
		await WaitForTimer(clock, 2);
		Assert.That(gateways, Has.Count.EqualTo(2));
		clock.Advance(TimeSpan.FromSeconds(30));
		await provider.StartupTask.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.That(gateways, Has.Count.EqualTo(3));
		Assert.That(started, Is.EqualTo(1));
		Assert.That(provider.SourcesDescription, Is.EqualTo("FRED, T-Invest (production)"));
		Assert.That(provider.MarketDataHub, Is.Not.Null);
		Assert.That(gateways.Count(g => g.Disposed), Is.EqualTo(2), "Failed attempts release their gateways.");
		var messages = logs.Entries.Select(e => e.Message).ToList();
		Assert.That(messages.Count(m => m.Contains("startup check failed", StringComparison.Ordinal)), Is.EqualTo(1), "The failure is logged once, not every 30 seconds.");
		Assert.That(messages, Has.One.Contains("startup check passed after 3 attempt"));
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task OperatorFixableFailuresAreLoggedOnceAndNeverRetried(bool unauthenticated)
	{
		var clock = new ManualTimeProvider();
		using var logs = new RecordingLoggerFactory();
		var gateways = new List<ScriptedTInvestGateway>();
		using var provider = Create(clock, logs, gateways, unauthenticated ? new TInvestRequestException("T-Invest token is missing, expired or for the other environment.", TInvestFailureKind.Configuration) : new ArgumentException("invented configuration error"));
		provider.Initialize();
		await provider.StartupTask.WaitAsync(TimeSpan.FromSeconds(10));
		clock.Advance(TimeSpan.FromMinutes(10));
		Assert.That(gateways, Has.Count.EqualTo(1));
		Assert.That(clock.ActiveTimers, Is.Zero);
		Assert.That(provider.SourcesDescription, Is.EqualTo("FRED"));
		Assert.That(logs.Entries.Count(e => e.Message.Contains("not configured", StringComparison.Ordinal)), Is.EqualTo(1));
		Assert.That(await provider.GetSymbolsStoreAsync("SYNTH-TESTCC", string.Empty), Is.Null, "After a permanent failure the symbol is simply unknown, as before.");
	}

	[Test]
	public async Task DisposalStopsTheRetryLoopAndNeverStartsTheSourceAfterwards()
	{
		var clock = new ManualTimeProvider();
		using var logs = new RecordingLoggerFactory();
		var gateways = new List<ScriptedTInvestGateway>();
		var provider = Create(clock, logs, gateways, new TInvestRequestException("T-Invest source request failed."));
		provider.Initialize();
		await WaitForTimer(clock);
		provider.Dispose();
		Assert.That(provider.StartupTask.IsCompleted, Is.True);
		clock.Advance(TimeSpan.FromMinutes(5));
		Assert.That(gateways, Has.Count.EqualTo(1), "No attempt after disposal.");
		Assert.That(provider.MarketDataHub, Is.Null);
		Assert.That(clock.ActiveTimers, Is.Zero);
	}

	[Test]
	public async Task ASessionRejectedWhileStartingIsToldWhenTheFeedBecomesAvailable()
	{
		var clock = new ManualTimeProvider();
		using var logs = new RecordingLoggerFactory();
		var gateways = new List<ScriptedTInvestGateway>();
		using var provider = Create(clock, logs, gateways, new TInvestRequestException("T-Invest source request failed."));
		var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		provider.TInvestStarted += () => signalled.TrySetResult();
		provider.Initialize();
		await WaitForTimer(clock);
		clock.Advance(TimeSpan.FromSeconds(30));
		await signalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.That(provider.SourcesDescription, Is.EqualTo("FRED, T-Invest (production)"));
	}

	internal static StoresProvider Create(ManualTimeProvider clock, RecordingLoggerFactory logs, List<ScriptedTInvestGateway> gateways, params Exception[] failures)
	{
		var script = new Queue<Exception>(failures);
		return new StoresProvider(
			new StoresProvider.StoresOptions { TInvest = new TInvestOptions { TokenSource = "Environment" } },
			logs,
			new TInvestToken("invented-startup-secret"),
			() =>
			{
				var gateway = new ScriptedTInvestGateway { Access = new(true, new[] { true }) };
				if (script.TryDequeue(out var failure)) gateway.AccessErrors.Enqueue(failure);
				gateways.Add(gateway);
				return gateway;
			},
			clock);
	}

	internal static async Task WaitForTimer(ManualTimeProvider clock, int attempts = 1)
	{
		for (var i = 0; i < 500 && clock.ActiveTimers == 0; i++) await Task.Delay(10);
		Assert.That(clock.ActiveTimers, Is.GreaterThan(0), "The loop is waiting for its next attempt (attempt " + attempts + ").");
	}
}
