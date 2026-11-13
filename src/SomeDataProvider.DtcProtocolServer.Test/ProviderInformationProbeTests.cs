namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Threading.Tasks;
using Dtc.Protocol.Private;
using NUnit.Framework;
using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;

public partial class ProviderInformationWireTests
{
	[TestCase(3)]
	[TestCase(4)]
	[TestCase(5)]
	public void AnIncompleteProbeSectionIsInvalidWithoutSourceWork(int missingBit)
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider(), enableProbes: true);
		using var client = Connect(host);
		var bytes = new byte[1024];
		var size = ProviderInformationCodec.EncodeVls(new ProviderHelloRequest { BudgetScopeID = "synthetic-generation", ProbeScopeID = "synthetic-probe", ProbeTInvestAttempts = 1, ProbeFredAttempts = 0 }, bytes);
		var presence = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16));
		System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), presence & ~(1UL << missingBit));
		client.GetStream().Write(bytes.AsSpan(0, size));
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("InvalidRequest"));
		Assert.That(gateway.Lookups, Is.Empty);
	}

	[Test]
	public void HistoricalSocketsChargeTheSameFredDispatchBudget()
	{
		var clock = new ManualTimeProvider();
		var calls = 0;
		using var http = new System.Net.Http.HttpClient(new FakeFredHandler(_ =>
		{
			calls++;
			return FakeFredHandler.JsonResponse("{\"categories\":[{\"id\":17,\"name\":\"Invented category\",\"parent_id\":0}]}");
		})) { BaseAddress = new Uri("https://synthetic.invalid/") };
		using var service = new SomeDataProvider.DataStorage.Fred.Service(http, (SomeDataProvider.DataStorage.Fred.ServiceApiKey)new string('a', 32), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
		using var store = new SomeDataProvider.DataStorage.Fred.Store(service, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
		using var provider = new Data.StoresProvider(store, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
		using var host = new TInvestTestHost(provider, clock, enableProbes: true);
		using var main = Connect(host);
		using var history = Connect(host);
		var hello = new ProviderHelloRequest { BudgetScopeID = "synthetic-generation", ProbeScopeID = "synthetic-probe", ProbeTInvestAttempts = 0, ProbeFredAttempts = 2 };
		Send(main, hello);
		Read(main);
		Send(history, hello);
		Read(history);
		Page(main, new SymbolCatalogRequest { Catalog = "fred", NodeID = "fred", PageSize = 2 });
		Page(history, new SymbolCatalogRequest { Catalog = "fred", NodeID = "fred", PageSize = 3 });
		Assert.That(calls, Is.EqualTo(2), "Different uncached page shapes spend the same run allowance.");
		Assert.That(provider.GetProbeScopes(clock).Open("synthetic-probe", 180, 0, 2, true).Remaining, Is.EqualTo((0, 0)));
	}

	[Test]
	public async Task ProbeStreamExpiryDoesNotCloseAHealthyUserStream()
	{
		var clock = new ManualTimeProvider();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		gateway.OpenStream = _ =>
		{
			ProbeContext.Admit(ProbeSource.TInvest);
			var stream = new ScriptedMarketDataStream { Sending = (_, _) => ProbeContext.Admit(ProbeSource.TInvest) };
			lock (gateway.Streams) gateway.Streams.Add(stream);
			return Task.FromResult<ITInvestMarketDataStream>(stream);
		};
		gateway.Snapshot = (_, _) =>
		{
			ProbeContext.Admit(ProbeSource.TInvest);
			return Task.FromResult(new TInvestMarketSnapshot());
		};
		using var host = new TInvestTestHost(gateway, clock, enableProbes: true);
		await using var user = host.CreateClient(heartbeatInterval: 60);
		await host.ConnectAsync(user);
		var subscribe = user.SubscribeMarketDataAsync("SYNTH", "TESTCC");
		await TInvestMarketDataHubTests.Pump(clock, () => subscribe.IsCompleted);
		await subscribe;
		using var probe = Connect(host);
		Send(probe, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation", ProbeScopeID = "synthetic-probe", ProbeLifetimeSeconds = 30, ProbeTInvestAttempts = 20, ProbeFredAttempts = 0 });
		Assert.That(Read(probe), Is.TypeOf<ProviderHelloResponse>());
		var bytes = new byte[1024];
		var size = new Dtc.Protocol.Messages.MarketDataRequest { SymbolID = 7, Symbol = "SYNTH", Exchange = "TESTCC", RequestAction = Dtc.Protocol.RequestActionEnum.SUBSCRIBE }.ToVls(bytes, System.Text.Encoding.UTF8);
		probe.GetStream().Write(bytes.AsSpan(0, size));
		await TInvestMarketDataHubTests.Pump(clock, () => probe.Available > 0);
		lock (gateway.Streams) Assert.That(gateway.Streams.Count, Is.EqualTo(2), "Probe recovery must not run under the user stream's owner.");
		clock.Advance(TimeSpan.FromSeconds(30));
		await TInvestMarketDataHubTests.Until(() => gateway.Streams[1].Disposed);
		Assert.That(gateway.Streams[0].Disposed, Is.False);
	}

	[Test]
	public void OptInHelloCreatesAndJoinsOneBudgetWithoutUpstreamWork()
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider(), enableProbes: true);
		using var main = Connect(host);
		using var history = Connect(host);
		var hello = new ProviderHelloRequest { BudgetScopeID = "synthetic-generation", ProbeScopeID = "synthetic-probe", ProbeTInvestAttempts = 30, ProbeFredAttempts = 6 };
		Send(main, hello);
		Assert.That(Read(main), Is.TypeOf<ProviderHelloResponse>());
		Send(history, hello);
		var joined = (ProviderHelloResponse)Read(history);
		Assert.That(joined.Features & ProviderInformationFeatures.ProbeBudgetV1, Is.Not.Zero);
		Assert.That(joined.ProbeScopeID, Is.EqualTo(hello.ProbeScopeID));
		Assert.That(joined.ProbeTInvestAttempts, Is.EqualTo(30));
		Assert.That(joined.ProbeFredAttempts, Is.EqualTo(6));
		Assert.That(gateway.Lookups, Is.Empty);
	}

	[Test]
	public void ARefusedProbeCannotDowngradeTheSameSocketToUnbudgetedWork()
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation", ProbeScopeID = "synthetic-probe", ProbeTInvestAttempts = 1, ProbeFredAttempts = 0 });
		Assert.That(Read(client), Is.TypeOf<ProviderRequestReject>());
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Assert.That(Read(client), Is.TypeOf<ProviderRequestReject>(), "A failed diagnostic hello must not become ordinary source work.");
		Assert.That(gateway.Lookups, Is.Empty);
	}
}
