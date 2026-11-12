namespace SomeDataProvider.DataStorage.TInvest.Test;

using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class InformationStoreTests
{
	[TestCase(0)]
	[TestCase(3600)]
	public void RetryBoundsAreInclusive(int seconds) =>
		Assert.That(new TInvestRequestException("Synthetic failure", retryAfterSeconds: seconds).RetryAfterSeconds, Is.EqualTo(seconds));

	[TestCase(-1)]
	[TestCase(3601)]
	public void RetryOutsideWireBoundsIsRejected(int seconds) =>
		Assert.Throws<ArgumentOutOfRangeException>(() => new TInvestRequestException("Synthetic failure", retryAfterSeconds: seconds));

	[Test]
	public async Task ContextDeadlineDoesNotWaitForAnUncooperativeGateway()
	{
		var clock = new ManualTimeProvider();
		var pending = new TaskCompletionSource<TInvestInstrument?>();
		using var gateway = new ScriptedTInvestGateway { Resolve = (_, _, _) => pending.Task };
		using var store = new TInvestStore(gateway, new TInvestOptions(), clock);
		var task = store.GetContextInstrumentAsync("SYNTH", "TESTCC", default);
		clock.Advance(TimeSpan.FromMilliseconds(7999));
		Assert.That(task.IsCompleted, Is.False);
		clock.Advance(TimeSpan.FromMilliseconds(1));
		var error = Assert.ThrowsAsync<TInvestRequestException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		Assert.That(error.RetryAfterSeconds, Is.EqualTo(1));
		pending.SetResult(ScriptedTInvestGateway.Invent());
		Assert.That(task.IsCompletedSuccessfully, Is.False);
	}

	[Test]
	public void ContextPreservesCallerCancellation()
	{
		var clock = new ManualTimeProvider();
		using var cancellation = new CancellationTokenSource();
		using var gateway = new ScriptedTInvestGateway { Resolve = (_, _, _) => new TaskCompletionSource<TInvestInstrument?>().Task };
		using var store = new TInvestStore(gateway, new TInvestOptions(), clock);
		var task = store.GetContextInstrumentAsync("SYNTH", "TESTCC", cancellation.Token);
		cancellation.Cancel();
		Assert.CatchAsync<OperationCanceledException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Test]
	public void ContextRejectsAnInconsistentSourceIdentity()
	{
		using var gateway = new ScriptedTInvestGateway { Resolve = (_, _, _) => Task.FromResult<TInvestInstrument?>(ScriptedTInvestGateway.Invent("SYNTHOTHER")) };
		using var store = new TInvestStore(gateway, new TInvestOptions());
		Assert.ThrowsAsync<TInvestRequestException>(() => store.GetContextInstrumentAsync("SYNTH", "TESTCC", default));
	}
}
