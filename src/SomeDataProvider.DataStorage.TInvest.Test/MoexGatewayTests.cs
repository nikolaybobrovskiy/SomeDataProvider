namespace SomeDataProvider.DataStorage.TInvest.Test;

using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;
using Tinkoff.InvestApi.V1;

[TestFixture]
public class MoexGatewayTests
{
	[TestCase(InstrumentType.Share, RealExchange.Moex, MoexMarket.Shares)]
	[TestCase(InstrumentType.Etf, RealExchange.Moex, MoexMarket.Funds)]
	[TestCase(InstrumentType.Bond, RealExchange.Moex, MoexMarket.Bonds)]
	[TestCase(InstrumentType.Futures, RealExchange.Moex, MoexMarket.Derivatives)]
	[TestCase(InstrumentType.Share, RealExchange.Rts, MoexMarket.Unknown)]
	[TestCase(InstrumentType.Share, RealExchange.Otc, MoexMarket.Unknown)]
	[TestCase(InstrumentType.Share, RealExchange.Dealer, MoexMarket.Unknown)]
	[TestCase(InstrumentType.Share, RealExchange.Unspecified, MoexMarket.Unknown)]
	[TestCase(InstrumentType.Currency, RealExchange.Moex, MoexMarket.Unknown)]
	[TestCase(InstrumentType.Commodity, RealExchange.Moex, MoexMarket.Unknown)]
	public async Task RuntimeSdkFieldsMapMarketsWithoutInferringAdmission(InstrumentType kind, RealExchange exchange, MoexMarket expected)
	{
		var clock = new ManualTimeProvider();
		var date = new DateOnly(2026, 11, 12);
		clock.Advance(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch);
		var invoker = new InstrumentInvoker(kind, exchange);
		using var gateway = new SdkTInvestGateway(invoker, new(), new TInvestRateLimiter(clock), clock);
		var instrument = await gateway.GetInstrumentAsync("SYNTH", "TESTCC", clock.GetUtcNow().AddSeconds(8), default);
		Assert.That(instrument!.Market, Is.EqualTo(expected));
		Assert.That(instrument.Calendar, Is.Null, "Current WeekendFlag and API availability do not establish dated session admission.");
		var calendar = await ((ITInvestGateway)gateway).GetTradingCalendarAsync(MoexCalendarSource.CalendarId(instrument), MoexCalendarSource.Revision,
			date, date, clock.GetUtcNow().AddSeconds(8), default);
		Assert.That(calendar, Is.Null);
		Assert.That(invoker.Calls, Is.EqualTo(kind is InstrumentType.Bond or InstrumentType.Futures ? 2 : 1), "Calendar reads must make no SDK calls.");
	}

	sealed class InstrumentInvoker(InstrumentType kind, RealExchange exchange) : CallInvoker
	{
		internal int Calls { get; private set; }
		public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
		{
			Calls++;
			object response = method.Name switch
			{
				"GetInstrumentBy" => new InstrumentResponse { Instrument = new Instrument { Ticker = "SYNTH", ClassCode = "TESTCC", Uid = "invented-uid-SYNTH", Name = "Invented instrument", InstrumentKind = kind, RealExchange = exchange, Exchange = "SYNTHSECTION", WeekendFlag = true, ApiTradeAvailableFlag = true } },
				"BondBy" => new BondResponse { Instrument = new Bond() },
				"FutureBy" => new FutureResponse { Instrument = new Future() },
				_ => throw new AssertionException("Unexpected SDK call: " + method.Name),
			};
			return new(Task.FromResult((TResponse)response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
		}

		public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
		public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
	}
}
