namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Protocol;
using Dtc.Protocol.Private;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Data;

public partial class ProviderInformationWireTests
{
	const string SyntheticFredSearch = "{\"seriess\":[{\"id\":\"SYNMAC\",\"title\":\"Invented macro series\",\"frequency_short\":\"M\",\"units_short\":\"Index\",\"seasonal_adjustment_short\":\"NSA\",\"popularity\":10}]}";

	[TestCase(true, true, false)]
	[TestCase(false, true, false)]
	[TestCase(true, false, false)]
	[TestCase(false, false, false)]
	[TestCase(true, true, true)]
	[TestCase(false, true, true)]
	[TestCase(true, false, true)]
	[TestCase(false, false, true)]
	public void MixedSearchKeepsHealthyRowsAndNegotiatedPartialStatus(bool failTInvest, bool negotiate, bool healthyEmpty)
	{
		var fredCalls = 0;
		var handler = new FakeFredHandler(_ =>
		{
			fredCalls++;
			return failTInvest ? FakeFredHandler.JsonResponse(healthyEmpty ? "{\"seriess\":[]}" : SyntheticFredSearch) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
		});
		using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fred.test/fred/") };
		var fred = new SomeDataProvider.DataStorage.Fred.Store(new SomeDataProvider.DataStorage.Fred.Service(http, (SomeDataProvider.DataStorage.Fred.ServiceApiKey)"invented-key", NullLoggerFactory.Instance), NullLoggerFactory.Instance);
		using var gateway = new ScriptedTInvestGateway();
		if (!healthyEmpty || failTInvest) gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		if (failTInvest) gateway.Resolve = (_, _, _) => throw new TInvestRequestException("Synthetic source failure.");
		using var store = new TInvestStore(gateway, new TInvestOptions());
		using var provider = new StoresProvider(fred, store, NullLoggerFactory.Instance);
		using var host = new TInvestTestHost(provider);
		using var client = Connect(host);
		if (negotiate)
		{
			Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
			Read(client);
		}
		client.GetStream().Write(DtcWire.EncodeSymbolSearchRequest(92, "query", searchType: SearchTypeEnum.SEARCH_TYPE_BY_DESCRIPTION));
		if (negotiate)
		{
			var status = (SourceOperationStatus)Read(client);
			Assert.That(status.RequestID, Is.EqualTo(92));
			Assert.That(status.Source, Is.EqualTo(failTInvest ? "t-invest" : "fred"));
			Assert.That(status.Status, Is.EqualTo(2), "Partial is nonterminal; the standard final response still follows.");
			Assert.That(status.ReasonCode, Is.EqualTo("SourceUnavailable"));
		}
		var frame = ReadStandardFrame(client);
		if (healthyEmpty)
		{
			Assert.That(DtcWire.TypeOf(frame), Is.EqualTo(DtcConstants.SECURITY_DEFINITION_REJECT), "A missing source prevents a definitive no-matches answer even when the healthy source has no rows.");
			Assert.That(fredCalls, Is.EqualTo(1));
			return;
		}
		Assert.That(DtcWire.TypeOf(frame), Is.EqualTo(DtcConstants.SECURITY_DEFINITION_RESPONSE));
		var (requestId, symbol) = DtcWire.DecodeSecurityDefinitionResponse(frame);
		Assert.That(requestId, Is.EqualTo(92));
		Assert.That(symbol, Is.EqualTo(healthyEmpty ? string.Empty : failTInvest ? "fred-SYNMAC" : "SYNTH"));
		Assert.That(DtcWire.DecodeSecurityDefinitionResponseIsFinalMessage(frame), Is.True);
		Assert.That(fredCalls, Is.EqualTo(1));
	}

	[Test]
	public void MixedSearchAllSourcesFailedIsUnavailableAndOneTerminalReject()
	{
		using var http = new HttpClient(new FakeFredHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))) { BaseAddress = new Uri("https://fred.test/fred/") };
		var fred = new SomeDataProvider.DataStorage.Fred.Store(new SomeDataProvider.DataStorage.Fred.Service(http, (SomeDataProvider.DataStorage.Fred.ServiceApiKey)"invented-key", NullLoggerFactory.Instance), NullLoggerFactory.Instance);
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		gateway.Resolve = (_, _, _) => throw new TInvestRequestException("Synthetic failure.", TInvestFailureKind.Limit, 3);
		using var store = new TInvestStore(gateway, new TInvestOptions());
		using var provider = new StoresProvider(fred, store, NullLoggerFactory.Instance);
		using var host = new TInvestTestHost(provider);
		using var client = Connect(host);
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
		Read(client);
		client.GetStream().Write(DtcWire.EncodeSymbolSearchRequest(93, "query", searchType: SearchTypeEnum.SEARCH_TYPE_BY_DESCRIPTION));
		var first = (SourceOperationStatus)Read(client);
		var second = (SourceOperationStatus)Read(client);
		Assert.That(first.Source, Is.EqualTo("t-invest"));
		Assert.That(first.ReasonCode, Is.EqualTo("LimitRejected"));
		Assert.That(first.RetryAfterSeconds, Is.EqualTo(3));
		Assert.That(second.Source, Is.EqualTo("fred"));
		Assert.That(new[] { first.Status, second.Status }, Is.All.EqualTo(3));
		Assert.That(new[] { first.RequestID, second.RequestID }, Is.All.EqualTo(93));
		Assert.That(DtcWire.TypeOf(ReadStandardFrame(client)), Is.EqualTo(DtcConstants.SECURITY_DEFINITION_REJECT));
		// A following request still works and proves no extra terminal response was queued.
		client.GetStream().Write(DtcWire.EncodeSymbolSearchRequest(94, string.Empty));
		Dtc.Protocol.Vls.DtcVlsDecoder.Decode(ReadStandardFrame(client), out Dtc.Protocol.Vls.s_SecurityDefinitionReject reject);
		Assert.That(reject.RequestID, Is.EqualTo(94));
	}

	[Test]
	public void MixedSearchCallerCancellationDoesNotFallThroughToFred()
	{
		var fredCalls = 0;
		using var http = new HttpClient(new FakeFredHandler(_ =>
		{
			fredCalls++;
			return FakeFredHandler.JsonResponse(SyntheticFredSearch);
		})) { BaseAddress = new Uri("https://fred.test/fred/") };
		var fred = new SomeDataProvider.DataStorage.Fred.Store(new SomeDataProvider.DataStorage.Fred.Service(http, (SomeDataProvider.DataStorage.Fred.ServiceApiKey)"invented-key", NullLoggerFactory.Instance), NullLoggerFactory.Instance);
		using var canceled = new CancellationTokenSource();
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		gateway.Resolve = (_, _, _) =>
		{
			canceled.Cancel();
			throw new OperationCanceledException(canceled.Token);
		};
		using var store = new TInvestStore(gateway, new TInvestOptions());
		using var provider = new StoresProvider(fred, store, NullLoggerFactory.Instance);
		Assert.ThrowsAsync<OperationCanceledException>(async () => await provider.SearchSymbolsAsync("query", SymbolSearchType.ByDescription, 50, canceled.Token));
		Assert.That(fredCalls, Is.Zero);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void MixedSearchRejectsWhitespaceBeforeEitherSource(bool direct)
	{
		var fredCalls = 0;
		using var http = new HttpClient(new FakeFredHandler(_ =>
		{
			fredCalls++;
			return FakeFredHandler.JsonResponse(SyntheticFredSearch);
		})) { BaseAddress = new Uri("https://fred.test/fred/") };
		var fred = new SomeDataProvider.DataStorage.Fred.Store(new SomeDataProvider.DataStorage.Fred.Service(http, (SomeDataProvider.DataStorage.Fred.ServiceApiKey)"invented-key", NullLoggerFactory.Instance), NullLoggerFactory.Instance);
		using var gateway = new ScriptedTInvestGateway();
		using var store = new TInvestStore(gateway, new TInvestOptions());
		using var provider = new StoresProvider(fred, store, NullLoggerFactory.Instance);
		if (direct)
		{
			Assert.That(Assert.ThrowsAsync<TInvestRequestException>(async () => await provider.SearchSymbolsAsync(" 	 ", SymbolSearchType.ByDescription, 50))!.Message, Is.EqualTo("SearchText must not be empty."));
		}
		else
		{
			using var host = new TInvestTestHost(provider);
			using var client = Connect(host);
			Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
			Read(client);
			client.GetStream().Write(DtcWire.EncodeSymbolSearchRequest(95, " 	 "));
			var frame = ReadStandardFrame(client);
			Assert.That(DtcWire.TypeOf(frame), Is.EqualTo(DtcConstants.SECURITY_DEFINITION_REJECT), "Malformed input is not a source failure/status.");
			Assert.That(DtcWire.DecodeSecurityDefinitionRejectText(frame), Is.EqualTo("SearchText must not be empty."));
		}
		Assert.That(gateway.Searches, Is.Zero);
		Assert.That(fredCalls, Is.Zero);
	}

	[Test]
	public void UnavailableSearchKeepsALocallyAuthoredRetryHintForLegacyClients()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		gateway.Resolve = (_, _, _) => throw new TInvestRequestException("Synthetic source payload must not be echoed.", TInvestFailureKind.Limit, 3);
		using var host = new TInvestTestHost(gateway);
		using var client = Connect(host);
		client.GetStream().Write(DtcWire.EncodeSymbolSearchRequest(96, "query", exchange: "TESTCC", searchType: SearchTypeEnum.SEARCH_TYPE_BY_DESCRIPTION));
		Assert.That(DtcWire.DecodeSecurityDefinitionRejectText(ReadStandardFrame(client)), Is.EqualTo("Symbol search sources are unavailable; retry in 3 s."));
	}
}
