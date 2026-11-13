namespace SomeDataProvider.DataStorage.TInvest.Test;

using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;
using Tinkoff.InvestApi.V1;

[TestFixture]
public class CatalogGatewayTests
{
	[TestCase(TInvestInstrumentKind.Share)]
	[TestCase(TInvestInstrumentKind.Bond)]
	[TestCase(TInvestInstrumentKind.Etf)]
	[TestCase(TInvestInstrumentKind.Future)]
	[TestCase(TInvestInstrumentKind.Currency)]
	public async Task BulkListUsesOneBudgetedCallAndMatchesExactContextData(TInvestInstrumentKind kind)
	{
		object detail = kind switch
		{
			TInvestInstrumentKind.Share => new Share(), TInvestInstrumentKind.Bond => new Bond(), TInvestInstrumentKind.Etf => new Etf(),
			TInvestInstrumentKind.Future => new Future(), _ => new Currency(),
		};
		var generic = new Instrument();
		foreach (var target in new[] { detail, generic })
		{
			void Set(string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
			Set("Ticker", "SYNTH");
			Set("ClassCode", "TESTCC");
			Set("Uid", "invented-bulk-uid");
			Set("Figi", "invented-figi");
			Set("Name", "Invented bulk instrument");
			Set("Lot", 10);
			Set("ApiTradeAvailableFlag", true);
			Set(target is Currency ? "Currency_" : "Currency", "zzz");
			Set("MinPriceIncrement", new Quotation { Nano = 10_000_000 });
			Set("First1MinCandleDate", Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(2)));
			Set("First1DayCandleDate", Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(1)));
		}
		generic.InstrumentKind = kind switch
		{
			TInvestInstrumentKind.Share => InstrumentType.Share, TInvestInstrumentKind.Bond => InstrumentType.Bond,
			TInvestInstrumentKind.Etf => InstrumentType.Etf, TInvestInstrumentKind.Future => InstrumentType.Futures, _ => InstrumentType.Currency,
		};
		if (detail is Bond bond) bond.Nominal = new MoneyValue { Units = 1000, Currency = "zzz" };
		if (detail is Future future)
		{
			future.MinPriceIncrementAmount = new Quotation { Nano = 250_000_000 };
			future.BasicAssetSize = new Quotation { Units = 5 };
			future.ExpirationDate = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(90));
		}
		var invoker = new Invoker(name => name switch
		{
			"Shares" => new SharesResponse { Instruments = { (Share)detail } }, "Bonds" => new BondsResponse { Instruments = { (Bond)detail } },
			"Etfs" => new EtfsResponse { Instruments = { (Etf)detail } }, "Futures" => new FuturesResponse { Instruments = { (Future)detail } },
			"Currencies" => new CurrenciesResponse { Instruments = { (Currency)detail } },
			"GetInstrumentBy" => new InstrumentResponse { Instrument = generic },
			"BondBy" => new BondResponse { Instrument = (Bond)detail }, "FutureBy" => new FutureResponse { Instrument = (Future)detail },
			_ => throw new AssertionException("Unexpected upstream call."),
		});
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		using var sdk = new SdkTInvestGateway(invoker, new(), limiter, clock);
		ITInvestGateway gateway = sdk;
		Assert.That(gateway.SupportsCatalog, Is.True);
		var rows = await gateway.ListInstrumentsAsync(kind, clock.GetUtcNow().AddSeconds(60), default);
		Assert.That(invoker.Requests.Count, Is.EqualTo(1));
		Assert.That(((InstrumentsRequest)invoker.Requests[0]).InstrumentStatus, Is.EqualTo(InstrumentStatus.All));
		Assert.That(limiter.InspectTraffic().Address.Count, Is.EqualTo(1));
		var exact = await gateway.GetInstrumentAsync("SYNTH", "TESTCC", clock.GetUtcNow().AddSeconds(60), default);
		Assert.That(rows.Single(), Is.EqualTo(exact));
	}

	sealed class Invoker(Func<string, object> script) : CallInvoker
	{
		public List<object> Requests { get; } = new();
		public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
		{
			Requests.Add(request!);
			return new(Task.FromResult((TResponse)script(method.Name)), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
		}
		public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
		public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
	}
}
