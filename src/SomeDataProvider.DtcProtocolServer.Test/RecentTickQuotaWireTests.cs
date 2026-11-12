namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Dtc.Protocol;
using Dtc.Protocol.Messages;
using Dtc.Protocol.Private;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;

public partial class ProviderInformationWireTests
{
	[Test]
	public void RecentProofPrecedesTheStandardHeaderAndExactTicksWithOneFinal()
	{
		using var fixture = new RecentWireFixture();
		using var history = fixture.Join();
		var trade = new TInvestTrade(fixture.Request.InstrumentUID, new(fixture.Request.From, 123456000), 101.25, 3, TInvestDirection.Buy, true);
		fixture.Gateway.LastTrades = (_, _) => new[] { trade, trade };
		Send(history, fixture.Request);
		var status = (SourceOperationStatus)Read(history);
		Assert.That(status.RequestID, Is.EqualTo(fixture.Request.RequestID));
		DtcBinaryDecoder.Decode<s_HistoricalPriceDataResponseHeader>(ReadStandardFrame(history), out var header);
		Assert.That(header.RequestID, Is.EqualTo(fixture.Request.RequestID));
		Assert.That(header.NoRecordsToReturn, Is.Zero);
		for (var i = 0; i < 2; i++)
		{
			DtcBinaryDecoder.Decode<s_HistoricalPriceDataTickRecordResponse>(ReadStandardFrame(history), out var tick);
			Assert.That(tick.RequestID, Is.EqualTo(fixture.Request.RequestID));
			Assert.That(tick.Price, Is.EqualTo(101.25));
			Assert.That(tick.Volume, Is.EqualTo(30));
			Assert.That(tick.DateTime, Is.EqualTo(trade.Time.Microseconds / 1_000_000.0));
			Assert.That(tick.AtBidOrAsk, Is.EqualTo(AtBidOrAskEnum.AT_ASK));
			Assert.That(tick.IsFinalRecord, Is.EqualTo(i));
		}
		Send(history, fixture.Refresh());
		Assert.That(Read(history), Is.TypeOf<ProviderHelloResponse>());
	}

	[Test]
	public void ClockRefreshKeepsNegotiationAndSharesTheRequestBudgetAcrossSocketsAndReconnects()
	{
		using var fixture = new RecentWireFixture(2);
		using var oldGeneration = fixture.Join();
		fixture.Clock.Advance(TimeSpan.FromHours(3));
		var refresh = fixture.Refresh();
		Send(oldGeneration, refresh);
		var first = (ProviderHelloResponse)Read(oldGeneration);
		Assert.That(first.Mode, Is.EqualTo(ProviderHelloModes.ClockOnly));
		Assert.That(first.SelectedVersion, Is.EqualTo(1));
		Assert.That(first.Features, Is.EqualTo(fixture.Hello.Features));
		Assert.That(first.BudgetScopeID, Is.EqualTo(fixture.Request.BudgetScopeID));
		Assert.That(first.InstrumentUID, Is.EqualTo(fixture.Request.InstrumentUID));
		Assert.That(first.UIDEpoch, Is.EqualTo(fixture.Request.UIDEpoch));
		Assert.That(first.ProviderUtcMicroseconds, Is.EqualTo((fixture.Clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks / 10));
		using var newGeneration = fixture.Join();
		Send(newGeneration, fixture.RequestNow());
		Assert.That(Read(newGeneration), Is.TypeOf<SourceOperationStatus>());
		ReadStandardFrame(newGeneration);
		using var reconnect = fixture.Join();
		Send(reconnect, refresh);
		Assert.That(((ProviderRequestReject)Read(reconnect)).ReasonCode, Is.EqualTo("BudgetExhausted"));
		Assert.That(fixture.Gateway.LastTradeRequests, Has.Count.EqualTo(1));
		Assert.That(fixture.Gateway.Lookups, Has.Count.EqualTo(1));
	}

	[Test]
	public void ClockRefreshOnTheNegotiatingSocketEchoesIdentityWithoutJoiningOrResettingItsBudget()
	{
		using var fixture = new RecentWireFixture(1);
		fixture.Clock.Advance(TimeSpan.FromHours(3));
		Send(fixture.Main, fixture.Refresh());
		var answer = Read(fixture.Main);
		Assert.That(answer, Is.TypeOf<ProviderHelloResponse>());
		var refreshed = (ProviderHelloResponse)answer;
		Assert.That(refreshed.Mode, Is.EqualTo(ProviderHelloModes.ClockOnly));
		Assert.That(refreshed.Source, Is.EqualTo(fixture.Request.Source));
		Assert.That(refreshed.InstrumentUID, Is.EqualTo(fixture.Request.InstrumentUID));
		Assert.That(refreshed.UIDEpoch, Is.EqualTo(fixture.Request.UIDEpoch));
		Assert.That(refreshed.SelectedVersion, Is.EqualTo(fixture.Hello.SelectedVersion));
		Assert.That(refreshed.Features, Is.EqualTo(fixture.Hello.Features));
		Assert.That(refreshed.BudgetScopeID, Is.EqualTo(fixture.Hello.BudgetScopeID));
		Assert.That(refreshed.ProviderUtcMicroseconds, Is.EqualTo((fixture.Clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks / 10));
		Send(fixture.Main, fixture.RequestNow());
		Assert.That(((ProviderRequestReject)Read(fixture.Main)).ReasonCode, Is.EqualTo("FeatureUnavailable"), "A refresh does not turn the main socket into a historical join.");
		Send(fixture.Main, fixture.Refresh());
		Assert.That(((ProviderRequestReject)Read(fixture.Main)).ReasonCode, Is.EqualTo("BudgetExhausted"));
		Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
		Assert.That(fixture.Gateway.Lookups, Has.Count.EqualTo(1));
	}

	[TestCase(ProviderHelloModes.JoinHistorical, false)]
	[TestCase(ProviderHelloModes.ClockOnly, false)]
	[TestCase(ProviderHelloModes.ClockOnly, true)]
	public void ReacquiredChangedContextRejectsTheOldEpochOnHello(int mode, bool joined)
	{
		using var fixture = new RecentWireFixture();
		using var historical = mode == ProviderHelloModes.JoinHistorical ? Connect(fixture.Host) : joined ? fixture.Join() : null;
		var client = historical ?? fixture.Main;
		var staleHello = fixture.Refresh() with { RequestID = 74, Mode = mode };
		fixture.Gateway.Instruments[0] = fixture.Gateway.Instruments[0] with { Increment = 0.05m };
		Send(fixture.Main, new InstrumentContextRequest { Symbol = fixture.Request.Symbol });
		var context = (InstrumentContextResponse)Read(fixture.Main);
		Assert.That(context.InstrumentUID, Is.EqualTo(staleHello.InstrumentUID));
		Assert.That(context.UIDEpoch, Is.Not.EqualTo(staleHello.UIDEpoch));
		Assert.That(context.ExactIncrement, Is.EqualTo("0.05"));
		Assert.That(fixture.Host.Store.GetCachedContextInstrumentByUid(context.InstrumentUID!), Is.Not.Null);
		Assert.That(fixture.Gateway.Lookups, Has.Count.EqualTo(2));

		// On a joined socket this still matches its original binding, so only the changed cached epoch can reject it.
		Send(client, staleHello);
		var answer = Read(client);
		Assert.That(answer, Is.TypeOf<ProviderRequestReject>());
		var reject = (ProviderRequestReject)answer;
		Assert.That(reject.RequestID, Is.EqualTo(staleHello.RequestID));
		Assert.That(reject.FailedOperation, Is.EqualTo(staleHello.Type));
		Assert.That(reject.ReasonCode, Is.EqualTo("EpochMismatch"));

		// The current epoch remains usable; a rejected fresh join did not consume negotiation.
		using var currentHistory = joined ? Connect(fixture.Host) : null;
		var currentClient = currentHistory ?? client;
		var currentHello = staleHello with { UIDEpoch = context.UIDEpoch, Mode = joined ? ProviderHelloModes.JoinHistorical : mode };
		Send(currentClient, currentHello);
		var accepted = Read(currentClient);
		Assert.That(accepted, Is.TypeOf<ProviderHelloResponse>());
		Assert.That(((ProviderHelloResponse)accepted).UIDEpoch, Is.EqualTo(context.UIDEpoch));
		Assert.That(fixture.Gateway.Lookups, Has.Count.EqualTo(2), "Hello validation must not reacquire context implicitly.");
		Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
		Assert.That(fixture.Gateway.ArchiveRequests, Is.Empty);
		Assert.That(fixture.Gateway.CandleRequests, Is.Empty);
	}

	[TestCase(ProviderHelloModes.JoinHistorical, false)]
	[TestCase(ProviderHelloModes.ClockOnly, false)]
	[TestCase(ProviderHelloModes.ClockOnly, true)]
	public async Task EvictedHelloContextRequiresExplicitAcquisitionWithoutChangingItsEpoch(int mode, bool joined)
	{
		using var fixture = new RecentWireFixture();
		using var historical = mode == ProviderHelloModes.JoinHistorical ? Connect(fixture.Host) : joined ? fixture.Join() : null;
		var client = historical ?? fixture.Main;
		await fixture.EvictContextAsync();
		var lookups = fixture.Gateway.Lookups.Count;
		var hello = fixture.Refresh() with { Mode = mode };
		Send(client, hello);
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("ContextRequired"));
		Assert.That(fixture.Gateway.Lookups.Count, Is.EqualTo(lookups));
		Send(fixture.Main, new InstrumentContextRequest { Symbol = fixture.Request.Symbol });
		Assert.That(((InstrumentContextResponse)Read(fixture.Main)).UIDEpoch, Is.EqualTo(fixture.Request.UIDEpoch));
		Send(client, hello);
		Assert.That(Read(client), Is.TypeOf<ProviderHelloResponse>(), "Reacquisition re-enables the unchanged binding and a failed join did not consume negotiation.");
		Assert.That(fixture.Gateway.Lookups.Count, Is.EqualTo(lookups + 1));
		Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
	}

	[TestCase("scope")]
	[TestCase("uid")]
	public async Task JoinedHelloBindingMismatchTakesPriorityOverAnEvictedContext(string field)
	{
		using var fixture = new RecentWireFixture();
		using var history = fixture.Join();
		await fixture.EvictContextAsync();
		var lookups = fixture.Gateway.Lookups.Count;
		var refresh = field == "scope" ? fixture.Refresh() with { BudgetScopeID = "synthetic-other-profile" }
			: fixture.Refresh() with { InstrumentUID = "synthetic-other-uid" };
		Send(history, refresh);
		Assert.That(((ProviderRequestReject)Read(history)).ReasonCode, Is.EqualTo("EpochMismatch"));
		Assert.That(fixture.Gateway.Lookups.Count, Is.EqualTo(lookups));
		Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
	}

	[Test]
	public async Task OverlappingGenerationsCannotDispatchOrRefreshInParallel()
	{
		using var fixture = new RecentWireFixture(3);
		using var oldGeneration = fixture.Join();
		using var newGeneration = fixture.Join();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Gateway.PrepareRecent = async ct =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(ct);
		};
		try
		{
			Send(oldGeneration, fixture.Request);
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Send(newGeneration, fixture.Request);
			Assert.That(((ProviderRequestReject)Read(newGeneration)).ReasonCode, Is.EqualTo("ReconcileBusy"));
			Send(newGeneration, fixture.Refresh());
			Assert.That(((ProviderRequestReject)Read(newGeneration)).ReasonCode, Is.EqualTo("ReconcileBusy"));
			release.TrySetResult();
			Assert.That(Read(oldGeneration), Is.TypeOf<SourceOperationStatus>());
			ReadStandardFrame(oldGeneration);
			using var reconnect = fixture.Join();
			Send(reconnect, fixture.Request);
			Assert.That(((ProviderRequestReject)Read(reconnect)).ReasonCode, Is.EqualTo("BudgetExhausted"));
			Assert.That(fixture.Gateway.LastTradeRequests, Has.Count.EqualTo(1));
		}
		finally { release.TrySetResult(); }
	}

	[Test]
	public async Task ContextChangingDuringPreparationRejectsTheOldEpochBeforeProof()
	{
		using var fixture = new RecentWireFixture();
		using var history = fixture.Join();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Gateway.PrepareRecent = async ct =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(ct);
		};
		try
		{
			Send(history, fixture.Request);
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			fixture.Gateway.Instruments[0] = fixture.Gateway.Instruments[0] with { Increment = 0.05m };
			Send(fixture.Main, new InstrumentContextRequest { Symbol = "SYNTH-TESTCC" });
			Assert.That(((InstrumentContextResponse)Read(fixture.Main)).UIDEpoch, Is.Not.EqualTo(fixture.Request.UIDEpoch));
			release.TrySetResult();
			var answer = Read(history);
			Assert.That(answer, Is.TypeOf<ProviderRequestReject>());
			Assert.That(((ProviderRequestReject)answer).ReasonCode, Is.EqualTo("EpochMismatch"));
		}
		finally { release.TrySetResult(); }
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task EvictedContextRequiresExplicitAcquisitionBeforeOrDuringPreparation(bool duringPreparation)
	{
		using var fixture = new RecentWireFixture();
		using var history = fixture.Join();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Gateway.PrepareRecent = async ct =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(ct);
		};
		try
		{
			if (duringPreparation)
			{
				Send(history, fixture.Request);
				await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			}
			fixture.Gateway.Resolve = (ticker, code, _) => Task.FromResult<TInvestInstrument?>(ScriptedTInvestGateway.Invent(ticker, code));
			for (var i = 0; i < 4096; i++) await fixture.Host.Store.GetContextInstrumentAsync("SYNTH" + i, "TESTCC", default);
			Assert.That(fixture.Host.Store.GetCachedContextInstrument("SYNTH", "TESTCC"), Is.Null);
			var lookups = fixture.Gateway.Lookups.Count;
			if (!duringPreparation) Send(history, fixture.Request);
			release.TrySetResult();
			var answer = Read(history);
			Assert.That(answer, Is.TypeOf<ProviderRequestReject>());
			Assert.That(((ProviderRequestReject)answer).ReasonCode, Is.EqualTo("ContextRequired"));
			Assert.That(fixture.Gateway.Lookups.Count, Is.EqualTo(lookups), "The recent lane does not rehydrate its evicted context.");
			Assert.That(fixture.Gateway.ArchiveRequests, Is.Empty);
			Assert.That(fixture.Gateway.CandleRequests, Is.Empty);
		}
		finally { release.TrySetResult(); }
	}

	[TestCase("epoch")]
	[TestCase("scope")]
	[TestCase("semantics")]
	public void InvalidRecentBindingRejectsWithoutLookingUpOrDispatching(string field)
	{
		using var fixture = new RecentWireFixture();
		using var history = fixture.Join();
		var request = field switch
		{
			"epoch" => fixture.Request with { UIDEpoch = "synthetic-other-epoch" },
			"scope" => fixture.Request with { BudgetScopeID = "synthetic-other-profile" },
			_ => fixture.Request with { SourceSemanticsID = "synthetic-other-semantics" },
		};
		Send(history, request);
		Assert.That(((ProviderRequestReject)Read(history)).ReasonCode, Is.EqualTo("EpochMismatch"));
		Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
		Assert.That(fixture.Gateway.Lookups, Has.Count.EqualTo(1));
	}

	[Test]
	public void ClockRefreshFailureDoesNotRenegotiateOrResetTheLedger()
	{
		using var fixture = new RecentWireFixture(1);
		using var history = fixture.Join();
		Send(history, fixture.Refresh() with { UIDEpoch = "synthetic-other-epoch" });
		Assert.That(((ProviderRequestReject)Read(history)).ReasonCode, Is.EqualTo("EpochMismatch"));
		Send(history, fixture.Refresh());
		Assert.That(((ProviderRequestReject)Read(history)).ReasonCode, Is.EqualTo("BudgetExhausted"));
		Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
	}

	[Test]
	public void ClockCorrectionRefreshesTheSampleAndRejectsTheOldCutWithoutClamping()
	{
		using var fixture = new RecentWireFixture();
		using var history = fixture.Join();
		fixture.Clock.Correction = TimeSpan.FromSeconds(-10);
		Send(history, fixture.Refresh());
		var refreshed = (ProviderHelloResponse)Read(history);
		Assert.That(refreshed.ProviderUtcMicroseconds, Is.EqualTo((fixture.Clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks / 10));
		Assert.That(refreshed.Features, Is.EqualTo(fixture.Hello.Features));
		Send(history, fixture.Request);
		Assert.That(((ProviderRequestReject)Read(history)).ReasonCode, Is.EqualTo("ClockMismatch"));
		Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
		Send(history, fixture.RequestNow());
		Assert.That(Read(history), Is.TypeOf<SourceOperationStatus>());
		ReadStandardFrame(history);
	}

	[Test]
	public void RecentRequestOnAnUnjoinedSocketCannotUseTheAdvertisedBit()
	{
		using var fixture = new RecentWireFixture();
		Send(fixture.Main, fixture.Request);
		Assert.That(((ProviderRequestReject)Read(fixture.Main)).ReasonCode, Is.EqualTo("FeatureUnavailable"));
		Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
	}

	[TestCase("source")]
	[TestCase("expiry")]
	[TestCase("cap")]
	public void FailedRecentAnswerIsOneRejectBeforeAnyProofOrHeader(string failure)
	{
		using var fixture = new RecentWireFixture();
		using var history = fixture.Join();
		var request = fixture.Request;
		if (failure == "source") fixture.Gateway.LastTrades = (_, _) => throw new InvalidOperationException("Synthetic unsafe upstream detail");
		if (failure == "expiry")
		{
			request = request with { From = fixture.Clock.GetUtcNow().AddMinutes(-58).ToUnixTimeSeconds() };
			fixture.Gateway.BeforeRecentAdmission = () => fixture.Clock.Advance(TimeSpan.FromSeconds(1));
		}
		if (failure == "cap") fixture.Gateway.LastTrades = (_, _) => Enumerable.Repeat(new TInvestTrade(request.InstrumentUID, new(request.From, 0), 100, 1, TInvestDirection.Buy, true), 17).ToArray();
		Send(history, request);
		var reject = (ProviderRequestReject)Read(history);
		Assert.That(reject.RequestID, Is.EqualTo(request.RequestID));
		Assert.That(reject.FailedOperation, Is.EqualTo(20114));
		Assert.That(reject.ReasonCode, Is.EqualTo(failure switch { "source" => "SourceUnavailable", "expiry" => "RecentWindowExpired", _ => "TickCountCap" }));
		Assert.That(reject.Text, Does.Not.Contain("unsafe"));
		// A subsequent refresh, rather than a second terminal or partial response, is the next frame.
		Send(history, fixture.Refresh());
		Assert.That(Read(history), Is.TypeOf<ProviderHelloResponse>());
		Assert.That(fixture.Gateway.ArchiveRequests, Is.Empty);
		Assert.That(fixture.Gateway.CandleRequests, Is.Empty);
	}

	sealed class RecentWireFixture : IDisposable
	{
		public RecentWireFixture(int quota = 6)
		{
			Clock.Advance(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch);
			Gateway = new() { Limiter = new(Clock) };
			Gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
			Host = new(Gateway, Clock, new() { LiveReconcileRequestsPerMinute = quota }, onlyHistoryServer: true);
			Main = Connect(Host);
			Send(Main, new ProviderHelloRequest { BudgetScopeID = "synthetic-profile" });
			Hello = (ProviderHelloResponse)Read(Main);
			Send(Main, new InstrumentContextRequest { Symbol = "SYNTH-TESTCC" });
			var context = (InstrumentContextResponse)Read(Main);
			Request = new()
			{
				RequestID = 72, Symbol = context.Symbol, Source = context.Source, InstrumentUID = context.InstrumentUID!, UIDEpoch = context.UIDEpoch,
				SourceSemanticsID = context.SourceSemanticsID, BudgetScopeID = "synthetic-profile", From = Clock.GetUtcNow().AddMinutes(-5).ToUnixTimeSeconds(), To = Clock.GetUtcNow().AddSeconds(-2).ToUnixTimeSeconds(), MaximumTicks = 16,
			};
		}
		public CorrectedClock Clock { get; } = new();
		public ScriptedTInvestGateway Gateway { get; }
		public TInvestTestHost Host { get; }
		public TcpClient Main { get; }
		public ProviderHelloResponse Hello { get; }
		public RecentTickReconcileRequest Request { get; }
		public RecentTickReconcileRequest RequestNow() => Request with { From = Clock.GetUtcNow().AddMinutes(-5).ToUnixTimeSeconds(), To = Clock.GetUtcNow().AddSeconds(-2).ToUnixTimeSeconds() };
		public ProviderHelloRequest Refresh() => new() { Mode = ProviderHelloModes.ClockOnly, BudgetScopeID = Request.BudgetScopeID, Source = Request.Source, InstrumentUID = Request.InstrumentUID, UIDEpoch = Request.UIDEpoch };
		public TcpClient Join()
		{
			var history = Connect(Host);
			Send(history, Refresh() with { Mode = ProviderHelloModes.JoinHistorical });
			Assert.That(Read(history), Is.TypeOf<ProviderHelloResponse>());
			return history;
		}
		public async Task EvictContextAsync()
		{
			Gateway.Resolve = (ticker, code, _) => Task.FromResult<TInvestInstrument?>(ScriptedTInvestGateway.Invent(ticker, code));
			for (var i = 0; i < 4096; i++) await Host.Store.GetContextInstrumentAsync("SYNTH" + i, "TESTCC", default);
			Assert.That(Host.Store.GetCachedContextInstrument("SYNTH", "TESTCC"), Is.Null);
		}
		public void Dispose()
		{
			Main.Dispose();
			Host.Dispose();
		}
	}

	sealed class CorrectedClock : TimeProvider
	{
		readonly ManualTimeProvider _inner = new();
		public TimeSpan Correction { get; set; }
		public override long TimestampFrequency => _inner.TimestampFrequency;
		public override long GetTimestamp() => _inner.GetTimestamp();
		public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow() + Correction;
		public override System.Threading.ITimer CreateTimer(System.Threading.TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => _inner.CreateTimer(callback, state, dueTime, period);
		public void Advance(TimeSpan span) => _inner.Advance(span);
	}
}
