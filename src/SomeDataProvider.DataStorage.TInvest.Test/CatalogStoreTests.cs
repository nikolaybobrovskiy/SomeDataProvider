namespace SomeDataProvider.DataStorage.TInvest.Test;

using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class CatalogStoreTests
{
	[TestCase(1)]
	[TestCase(10)]
	public async Task AProbeCatalogFailureDoesNotPoisonTheSharedCatalogOrItsTimer(int ceiling)
	{
		var clock = new ManualTimeProvider();
		using var scopes = new SomeDataProvider.DataStorage.Definitions.ProbeScopeRegistry(clock);
		var scope = scopes.Open("synthetic-probe", 180, ceiling, 0);
		using var gateway = new ScriptedTInvestGateway();
		gateway.ListInstruments = async (_, _) =>
		{
			await Task.Yield();
			SomeDataProvider.DataStorage.Definitions.ProbeContext.Admit(SomeDataProvider.DataStorage.Definitions.ProbeSource.TInvest);
			return Array.Empty<TInvestInstrument>();
		};
		using var store = new TInvestStore(gateway, new(), clock);
		using (SomeDataProvider.DataStorage.Definitions.ProbeContext.Enter(scope))
		{
			if (ceiling == 1)
			{
				var error = Assert.CatchAsync(async () => await store.GetCatalogAsync(default));
				Assert.That(error, Is.TypeOf<SomeDataProvider.DataStorage.Definitions.ProbeRejectedException>().Or.InstanceOf<OperationCanceledException>());
			}
			else
			{
				Assert.That((await store.GetCatalogAsync(default)).Instruments, Is.Empty);
				Assert.That(scope.Remaining.TInvest, Is.EqualTo(5));
				clock.Advance(TimeSpan.FromHours(1));
				await store.CatalogRefreshTask;
				Assert.That(gateway.CatalogCalls.Count, Is.EqualTo(5), "A successful probe load must not schedule another source call on a periodic timer.");
			}
		}
		Assert.That((await store.GetCatalogAsync(default)).Instruments, Is.Empty);
		clock.Advance(TimeSpan.FromHours(1));
		await store.CatalogRefreshTask;
		Assert.That((await store.GetCatalogAsync(default)).Instruments, Is.Empty);
	}

	[Test]
	public async Task RefreshIsSingleFlightAndAFailureRetainsTheOldSnapshotAsStale()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent() with { Board = "TESTCC" });
		using var store = new TInvestStore(gateway, new(), clock);
		var old = await store.GetCatalogAsync(default);
		var pending = new TaskCompletionSource<IReadOnlyList<TInvestInstrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
		gateway.ListInstruments = (_, _) => pending.Task;
		clock.Advance(TimeSpan.FromHours(1));
		var refresh = store.CatalogRefreshTask;
		Assert.That(refresh.IsCompleted, Is.False);
		Assert.That(await store.GetCatalogAsync(default), Is.SameAs(old));
		Assert.That(await store.GetCatalogAsync(default), Is.SameAs(old));
		Assert.That(gateway.CatalogCalls.Count, Is.EqualTo(6));
		pending.SetException(new InvalidOperationException("Invented source failure."));
		await refresh;
		Assert.That(Assert.ThrowsAsync<TInvestCatalogException>(async () => await store.GetCatalogAsync(default))!.Reason, Is.EqualTo("CatalogStale"));
		Assert.That(old.Instruments.Count, Is.EqualTo(1));
		gateway.ListInstruments = null;
		clock.Advance(TimeSpan.FromMinutes(1));
		var recovered = await store.GetCatalogAsync(default);
		Assert.That(recovered.Version, Is.Not.EqualTo(old.Version));
		Assert.That(recovered.Instruments, Is.EqualTo(old.Instruments));
	}

	[Test]
	public async Task ColdLoadIsSharedAndACancelledCallerDoesNotCancelOtherReaders()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		var pending = new TaskCompletionSource<IReadOnlyList<TInvestInstrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
		gateway.ListInstruments = (_, _) => pending.Task;
		using var store = new TInvestStore(gateway, new(), clock);
		using var cancel = new CancellationTokenSource();
		var cancelled = store.GetCatalogAsync(cancel.Token);
		var healthy = store.GetCatalogAsync(default);
		cancel.Cancel();
		Assert.CatchAsync<OperationCanceledException>(async () => await cancelled);
		Assert.That(gateway.CatalogCalls.Count, Is.EqualTo(1));
		pending.SetResult(Array.Empty<TInvestInstrument>());
		Assert.That((await healthy).Instruments, Is.Empty);
		Assert.That(gateway.CatalogCalls.Count, Is.EqualTo(5));
	}

	[Test]
	public async Task AColdLoadTimeoutUsesTheFakeClockAndDisposalRemovesCatalogTimers()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		var pending = new TaskCompletionSource<IReadOnlyList<TInvestInstrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
		gateway.ListInstruments = (_, _) => pending.Task;
		using var store = new TInvestStore(gateway, new(), clock);
		var load = store.GetCatalogAsync(default);
		clock.Advance(TimeSpan.FromSeconds(60));
		Assert.That(Assert.ThrowsAsync<TInvestCatalogException>(async () => await load)!.Reason, Is.EqualTo("CatalogStale"));
		Assert.That(gateway.CatalogCalls.Count, Is.EqualTo(1));
		store.Dispose();
		await store.CatalogRefreshTask;
		Assert.That(clock.ActiveTimers, Is.Zero);
		pending.SetResult(Array.Empty<TInvestInstrument>());
	}

	[Test]
	public async Task ATokenCannotBeReplayedAgainstAnotherSourceScope()
	{
		using var gateway = new ScriptedTInvestGateway();
		using var first = new TInvestStore(gateway, new() { Environment = "Sandbox" }, new ManualTimeProvider());
		using var second = new TInvestStore(gateway, new() { Environment = "Production" }, new ManualTimeProvider());
		var snapshot = await first.GetCatalogAsync(default);
		var cursor = first.MakeCatalogCursor(snapshot, "t-bank", string.Empty, 200, 1);
		Assert.That(Assert.Throws<TInvestCatalogException>(() => second.ReadCatalogCursor(snapshot, "t-bank", string.Empty, 200, cursor))!.Reason, Is.EqualTo("InvalidCursor"));
	}

	[TestCase("conflict")]
	[TestCase("kind")]
	[TestCase("identity")]
	[TestCase("name")]
	public void InvalidBulkMetadataNeverPublishesAPartialSnapshot(string problem)
	{
		using var gateway = new ScriptedTInvestGateway();
		var valid = ScriptedTInvestGateway.Invent() with { Board = "TESTCC" };
		gateway.Instruments.Add(valid);
		var invalid = problem switch
		{
			"conflict" => valid with { Increment = 0.05m },
			"identity" => valid with { Short = valid.Short with { ClassCode = "TEST-INVALID" } },
			"name" => valid with { Short = valid.Short with { Name = new string('X', 1025) } },
			_ => valid with { Short = valid.Short with { Kind = TInvestInstrumentKind.Unsupported } },
		};
		gateway.ListInstruments = (kind, _) => Task.FromResult<IReadOnlyList<TInvestInstrument>>(kind == TInvestInstrumentKind.Share ? new[] { valid, invalid } : Array.Empty<TInvestInstrument>());
		using var store = new TInvestStore(gateway, new(), new ManualTimeProvider());
		Assert.That(Assert.ThrowsAsync<TInvestCatalogException>(async () => await store.GetCatalogAsync(default))!.Reason, Is.EqualTo("CatalogStale"));
	}

	[Test]
	public async Task SnapshotRefreshKeepsTheOldImmutableSnapshotUntilAllKindsComplete()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent() with { Board = "TESTCC" });
		using var store = new TInvestStore(gateway, new(), clock);
		var old = await store.GetCatalogAsync(default);
		gateway.Instruments[0] = gateway.Instruments[0] with { Increment = 0.05m };
		clock.Advance(TimeSpan.FromHours(1));
		var refreshed = await store.GetCatalogAsync(default);
		Assert.That(refreshed.Version, Is.Not.EqualTo(old.Version));
		Assert.That(old.Instruments.Single().Increment, Is.EqualTo(0.01m));
		Assert.That(refreshed.Instruments.Single().Increment, Is.EqualTo(0.05m));
	}
}
