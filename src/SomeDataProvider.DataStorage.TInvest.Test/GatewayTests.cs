namespace SomeDataProvider.DataStorage.TInvest.Test;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;
using Tinkoff.InvestApi.V1;

[TestFixture]
public class GatewayTests
{
	[TestCase("Sandbox", "sandbox-invest-public-api.tbank.ru")]
	[TestCase("Production", "invest-public-api.tbank.ru")]
	public async Task ChannelAppliesTlsCredentialsAndAppNameWithoutNetwork(string environment, string host)
	{
		var secret = "invented-credential-not-a-real-token";
		using var handler = new CaptureHandler(secret, host);
		var options = new TInvestOptions { Environment = environment };
		using var gateway = new SdkTInvestGateway(new TInvestToken(secret), options, new TInvestRateLimiter(), null, handler);
		await gateway.FindInstrumentsAsync("INVENTED", DateTimeOffset.UtcNow.AddSeconds(8), default);
		Assert.That(handler.Requests, Is.EqualTo(1));
		Assert.That(handler.CredentialsApplied, Is.True);
		Assert.That(handler.EndpointCorrect, Is.True);
	}

	[TestCase(StatusCode.Unavailable)]
	[TestCase(StatusCode.Internal)]
	[TestCase(StatusCode.Unauthenticated)]
	public void OneAttemptWithoutHiddenSdkRetries(StatusCode status)
	{
		var clock = new ManualTimeProvider();
		var invoker = new ScriptedInvoker((_, _) => throw new RpcException(new Status(status, "unsafe upstream detail")));
		using var gateway = NewGateway(invoker, clock);
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => gateway.FindInstrumentsAsync("INVENTED", clock.GetUtcNow().AddSeconds(8), default));
		Assert.That(invoker.Calls, Is.EqualTo(1));
		Assert.That(error!.ToString(), Does.Not.Contain("unsafe upstream detail"));
	}

	[TestCase(13)]
	[TestCase(14)]
	public void FailingRealChannelHasOneUpstreamAttempt(int status)
	{
		using var handler = new CaptureHandler("invented-credential", "sandbox-invest-public-api.tbank.ru", status);
		using var gateway = new SdkTInvestGateway(new TInvestToken("invented-credential"), new TInvestOptions { Environment = "Sandbox" }, new TInvestRateLimiter(), null, handler);
		Assert.ThrowsAsync<TInvestRequestException>(() => gateway.FindInstrumentsAsync("INVENTED", DateTimeOffset.UtcNow.AddSeconds(8), default));
		Assert.That(handler.Requests, Is.EqualTo(1));
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task ExhaustedRetriesOnlyOnceAndEveryAttemptIsAdmitted(bool failsAgain)
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		var invoker = new ScriptedInvoker((_, n) => n == 1 || failsAgain
			? throw new RpcException(new Status(StatusCode.ResourceExhausted, "80002"), Reset(1)) : new FindInstrumentResponse());
		using var gateway = new SdkTInvestGateway(invoker, new TInvestOptions(), limiter, clock);
		var task = gateway.FindInstrumentsAsync("INVENTED", clock.GetUtcNow().AddSeconds(8), default);
		Assert.That(invoker.Calls, Is.EqualTo(1));
		clock.Advance(TimeSpan.FromSeconds(1));
		if (failsAgain) Assert.ThrowsAsync<TInvestRequestException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
		else Assert.That(await task.WaitAsync(TimeSpan.FromSeconds(5)), Is.Empty);
		Assert.That(invoker.Calls, Is.EqualTo(2));
		// The retry consumed another address slot as well as its service slot.
		if (failsAgain) clock.Advance(TimeSpan.FromSeconds(1));
		var available = 0;
		while (limiter.TryAcquire(TInvestLimitGroup.Instruments) == TimeSpan.Zero) available++;
		Assert.That(available, Is.EqualTo(failsAgain ? 45 : 44));
	}

	[Test]
	public void ExhaustedDoesNotRetryWhenResetCannotFitDeadline()
	{
		var clock = new ManualTimeProvider();
		var invoker = new ScriptedInvoker((_, _) => throw new RpcException(new Status(StatusCode.ResourceExhausted, "80002"), Reset(60)));
		using var gateway = NewGateway(invoker, clock);
		Assert.ThrowsAsync<TInvestRequestException>(() => gateway.FindInstrumentsAsync("INVENTED", clock.GetUtcNow().AddSeconds(8), default));
		Assert.That(invoker.Calls, Is.EqualTo(1));
		Assert.That(clock.ActiveTimers, Is.Zero);
	}

	[TestCase(17)]
	[TestCase(1200)]
	[TestCase(3600)]
	public void SourceResetSurvivesDeadlineRefusalAsTypedRetry(int seconds)
	{
		var clock = new ManualTimeProvider();
		var invoker = new ScriptedInvoker((_, _) => throw new RpcException(new Status(StatusCode.ResourceExhausted, "80002"), Reset(seconds)));
		using var gateway = NewGateway(invoker, clock);
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => gateway.FindInstrumentsAsync("INVENTED", clock.GetUtcNow().AddSeconds(8), default));
		Assert.That(error!.RetryAfterSeconds, Is.EqualTo(seconds));
		Assert.That(invoker.Calls, Is.EqualTo(1));
		Assert.That(clock.ActiveTimers, Is.Zero);
	}

	[Test]
	public async Task ProductionCalendarSourceIsUnavailableWithoutAnyRpc()
	{
		var clock = new ManualTimeProvider();
		var invoker = new ScriptedInvoker((_, _) => throw new InvalidOperationException("Calendar rules must not be inferred from a venue call."));
		using var gateway = NewGateway(invoker, clock);
		var calendar = await ((ITInvestGateway)gateway).GetTradingCalendarAsync("synthetic-calendar", "synthetic-r1", CalendarTests.Friday, CalendarTests.Monday, clock.GetUtcNow().AddSeconds(8), default);
		Assert.That(calendar, Is.Null);
		Assert.That(invoker.Calls, Is.Zero);
	}

	[TestCase(InstrumentType.Share, TInvestInstrumentKind.Share)]
	[TestCase(InstrumentType.Etf, TInvestInstrumentKind.Etf)]
	[TestCase(InstrumentType.Currency, TInvestInstrumentKind.Currency)]
	[TestCase(InstrumentType.Bond, TInvestInstrumentKind.Bond)]
	[TestCase(InstrumentType.Futures, TInvestInstrumentKind.Future)]
	public async Task MapsInstrumentAndKindSpecificFields(InstrumentType kind, TInvestInstrumentKind expected)
	{
		var clock = new ManualTimeProvider();
		var expiry = new DateTimeOffset(2028, 6, 15, 12, 0, 0, TimeSpan.Zero);
		var invoker = new ScriptedInvoker((method, _) => method switch
		{
			"GetInstrumentBy" => new InstrumentResponse { Instrument = new Instrument { Ticker = "SYNTH", ClassCode = "TESTCC", Uid = "invented-uid", InstrumentKind = kind, Currency = "zzz", Lot = 10, MinPriceIncrement = new Quotation { Nano = 10_000_000 } } },
			"FutureBy" => new FutureResponse { Instrument = new Future { BasicAssetSize = new Quotation { Units = 5 }, MinPriceIncrementAmount = new Quotation { Nano = 250_000_000 }, ExpirationDate = Timestamp.FromDateTimeOffset(expiry) } },
			"BondBy" => new BondResponse { Instrument = new Bond { Nominal = new MoneyValue { Units = 1000 } } },
			_ => throw new InvalidOperationException("Unexpected scripted method."),
		});
		using var gateway = NewGateway(invoker, clock);
		var result = (await gateway.GetInstrumentAsync("SYNTH", "TESTCC", clock.GetUtcNow().AddSeconds(8), default))!;
		Assert.That(result.Short.Kind, Is.EqualTo(expected));
		Assert.That(result.Board, Is.EqualTo("TESTCC"));
		Assert.That(result.PriceUnit, Is.EqualTo(kind == InstrumentType.Futures ? "points" : kind == InstrumentType.Bond ? "percent-of-nominal" : "currency"));
		Assert.That(result.Calendar, Is.Null);
		Assert.That(result.DailyStampMode, Is.Zero);
		Assert.That(result.Increment, Is.EqualTo(0.01m));
		if (kind == InstrumentType.Futures)
		{
			Assert.That(result.IncrementAmount, Is.EqualTo(0.25m));
			Assert.That(result.BasicAssetSize, Is.EqualTo(5));
			Assert.That(result.Expiration, Is.EqualTo(expiry));
		}
		if (kind == InstrumentType.Bond) Assert.That(result.Nominal, Is.EqualTo(1000));
		Assert.That(invoker.Calls, Is.EqualTo(kind is InstrumentType.Bond or InstrumentType.Futures ? 2 : 1));
	}

	[TestCase(InstrumentType.Share, TInvestInstrumentKind.StockOption)]
	[TestCase(InstrumentType.Futures, TInvestInstrumentKind.FuturesOption)]
	[TestCase(InstrumentType.Index, TInvestInstrumentKind.IndexOption)]
	[TestCase(InstrumentType.Unspecified, TInvestInstrumentKind.Unsupported)]
	public async Task OptionKindIsRefinedBeforeDefinition(InstrumentType underlying, TInvestInstrumentKind expected)
	{
		var clock = new ManualTimeProvider();
		var invoker = new ScriptedInvoker((method, attempt) => method == "GetInstrumentBy"
			? new InstrumentResponse { Instrument = new Instrument { InstrumentKind = attempt == 1 ? InstrumentType.Option : underlying } }
			: new OptionResponse { Instrument = new Tinkoff.InvestApi.V1.Option { AssetType = "untrusted-asset-class", BasicAssetPositionUid = "invented-position", ExpirationDate = Timestamp.FromDateTimeOffset(clock.GetUtcNow().AddDays(10)) } });
		using var gateway = NewGateway(invoker, clock);
		var instrument = await gateway.GetInstrumentAsync("SYNTH", "TESTCC", clock.GetUtcNow().AddSeconds(8), default);
		Assert.That(instrument!.Short.Kind, Is.EqualTo(expected));
		Assert.That(instrument.Expiration.HasValue, Is.True);
		var underlyingRequest = (InstrumentRequest)invoker.Requests.Last();
		Assert.That(underlyingRequest.IdType, Is.EqualTo(InstrumentIdType.PositionUid));
		Assert.That(underlyingRequest.Id, Is.EqualTo("invented-position"));
	}

	[Test]
	public async Task SandboxCheckUsesGetInfoAndProductionUsesAccountsOnly()
	{
		var clock = new ManualTimeProvider();
		foreach (var sandbox in new[] { false, true })
		{
			var methods = new List<string>();
			var invoker = new ScriptedInvoker((method, _) =>
			{
				methods.Add(method);
				return sandbox ? new GetInfoResponse() : new GetAccountsResponse { Accounts = { new Account { AccessLevel = AccessLevel.AccountAccessLevelReadOnly } } };
			});
			using var gateway = new SdkTInvestGateway(invoker, new TInvestOptions { Environment = sandbox ? "Sandbox" : "Production" }, new TInvestRateLimiter(clock), clock);
			var access = await gateway.CheckAccessAsync(clock.GetUtcNow().AddSeconds(8), default);
			Assert.That(methods, Is.EqualTo(new[] { sandbox ? "GetInfo" : "GetAccounts" }));
			Assert.That(access.Authenticated, Is.True);
		}
	}

	[TestCase(SslPolicyErrors.RemoteCertificateNameMismatch)]
	[TestCase(SslPolicyErrors.RemoteCertificateNotAvailable)]
	[TestCase(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)]
	public void TlsNeverAcceptsWrongHostOrMissingCertificate(SslPolicyErrors errors) =>
		Assert.That(SdkTInvestGateway.ValidateCertificate(this, null, null, errors), Is.False);

	[TestCase(StatusCode.DeadlineExceeded)]
	[TestCase(StatusCode.Cancelled)]
	public void RpcBudgetExpiryIsALimitNotASourceFailure(StatusCode status)
	{
		var clock = new ManualTimeProvider();
		var invoker = new ScriptedInvoker((_, _) =>
		{
			clock.Advance(TimeSpan.FromSeconds(8));
			throw new RpcException(new Status(status, "scripted deadline"));
		});
		using var gateway = NewGateway(invoker, clock);
		var error = Assert.ThrowsAsync<TInvestRequestException>(() => gateway.FindInstrumentsAsync("SYNTH", clock.GetUtcNow().AddSeconds(8), default));
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		Assert.That(invoker.Calls, Is.EqualTo(1));
	}

	[Test]
	public void CallerCancellationDuringRpcIsNotAQuotaOrSourceFailure()
	{
		var clock = new ManualTimeProvider();
		using var cancel = new CancellationTokenSource();
		var invoker = new ScriptedInvoker((_, _) =>
		{
			cancel.Cancel();
			throw new RpcException(new Status(StatusCode.Cancelled, "scripted cancellation"));
		});
		using var gateway = NewGateway(invoker, clock);
		Assert.CatchAsync<OperationCanceledException>(() => gateway.FindInstrumentsAsync("SYNTH", clock.GetUtcNow().AddSeconds(8), cancel.Token));
		Assert.That(invoker.Calls, Is.EqualTo(1));
	}

	[Test]
	public async Task RealChannelCancellationSurfacesAsCancellationWithoutNetwork()
	{
		using var handler = new HangingHandler();
		using var cancel = new CancellationTokenSource();
		using var gateway = new SdkTInvestGateway(new TInvestToken("invented-credential"), new TInvestOptions { Environment = "Sandbox" }, new TInvestRateLimiter(), null, handler);
		var task = gateway.FindInstrumentsAsync("SYNTH", DateTimeOffset.UtcNow.AddSeconds(8), cancel.Token);
		await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cancel.Cancel();
		Assert.CatchAsync<OperationCanceledException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.That(handler.Requests, Is.EqualTo(1));
	}

	[TestCase(StatusCode.DeadlineExceeded)]
	[TestCase(StatusCode.Cancelled)]
	public async Task SearchKeepsCompletePrefixWhenSdkRpcExpiresMidHydration(StatusCode status)
	{
		var clock = new ManualTimeProvider();
		var invoker = new ScriptedInvoker((method, count) =>
		{
			if (method == "FindInstrument")
			{
				return new FindInstrumentResponse
				{
					Instruments =
					{
						new InstrumentShort { Ticker = "SYNTH0", ClassCode = "TESTCC", Uid = "invented0", InstrumentKind = InstrumentType.Share, Lot = 10, ApiTradeAvailableFlag = true },
						new InstrumentShort { Ticker = "SYNTH1", ClassCode = "TESTCC", Uid = "invented1", InstrumentKind = InstrumentType.Share, Lot = 10, ApiTradeAvailableFlag = true },
					},
				};
			}
			if (count == 2) return new InstrumentResponse { Instrument = new Instrument { Ticker = "SYNTH0", ClassCode = "TESTCC", Uid = "invented0", InstrumentKind = InstrumentType.Share, Lot = 10, Currency = "zzz", MinPriceIncrement = new Quotation { Nano = 10_000_000 } } };
			clock.Advance(TimeSpan.FromSeconds(8));
			throw new RpcException(new Status(status, "scripted deadline"));
		});
		using var gateway = NewGateway(invoker, clock);
		using var store = new TInvestStore(gateway, new TInvestOptions(), clock);
		var rows = await store.SearchSymbolsAsync("SYNTH", SomeDataProvider.DataStorage.Definitions.SymbolSearchType.BySymbol, 50);
		Assert.That(rows.Count, Is.EqualTo(1));
		Assert.That(rows.Single().Code, Is.EqualTo("SYNTH0"));
		Assert.That(rows.Single().MinPriceIncrement, Is.GreaterThan(0));
		Assert.That(invoker.Calls, Is.EqualTo(3));
	}

	[TestCase(StatusCode.NotFound)]
	[TestCase(StatusCode.InvalidArgument)]
	public void MissingOptionUnderlyingIsNegativeCachedAsUnsupported(StatusCode status)
	{
		var clock = new ManualTimeProvider();
		var invoker = new ScriptedInvoker((method, count) => count switch
		{
			1 => new InstrumentResponse { Instrument = new Instrument { Ticker = "SYNTH", ClassCode = "TESTCC", Uid = "invented-option", InstrumentKind = InstrumentType.Option, Lot = 10, Currency = "zzz", MinPriceIncrement = new Quotation { Nano = 10_000_000 } } },
			2 => new OptionResponse { Instrument = new Tinkoff.InvestApi.V1.Option { BasicAssetPositionUid = "invented-missing-position" } },
			_ => throw new RpcException(new Status(status, "scripted missing underlying")),
		});
		using var gateway = NewGateway(invoker, clock);
		using var store = new TInvestStore(gateway, new TInvestOptions(), clock);
		for (var i = 0; i < 2; i++)
		{
			var error = Assert.ThrowsAsync<TInvestRequestException>(async () => await store.GetSymbolAsync(TInvestStore.LookupCode("SYNTH", "TESTCC")));
			Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Unsupported));
		}
		Assert.That(invoker.Calls, Is.EqualTo(3));
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task RealChannelDeadlineWithoutCallerCancellationIsALimit(bool candles)
	{
		using var handler = new HangingHandler();
		using var gateway = new SdkTInvestGateway(new TInvestToken("invented-credential"), new TInvestOptions { Environment = "Sandbox" }, new TInvestRateLimiter(), null, handler);
		using var reservation = await gateway.ReserveCandlesAsync(1, DateTimeOffset.UtcNow.AddSeconds(20), default);
		// gRPC's native deadline uses wall time; no sleeps or network are involved.
		// Check the actual channel semantics too: both RPC status fallbacks remain defensive in CallAsync.
		var invoker = gateway.Channel!.CreateCallInvoker();
		Assert.CatchAsync<OperationCanceledException>(async () =>
		{
			var expires = DateTime.UtcNow.AddSeconds(1);
			if (candles) await new MarketDataService.MarketDataServiceClient(invoker).GetCandlesAsync(new GetCandlesRequest(), deadline: expires);
			else await new InstrumentsService.InstrumentsServiceClient(invoker).GetInstrumentByAsync(new InstrumentRequest(), deadline: expires);
		});
		var deadline = DateTimeOffset.UtcNow.AddSeconds(1);
		var error = Assert.ThrowsAsync<TInvestRequestException>(async () =>
		{
			if (candles) await gateway.GetCandlesAsync(new("invented-uid", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, 60, 2400, false), reservation, deadline, default);
			else await gateway.GetInstrumentAsync("SYNTH", "TESTCC", deadline, default);
		});
		Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		Assert.That(handler.Requests, Is.EqualTo(2));
	}

	[TestCase(false, false)]
	[TestCase(true, false)]
	[TestCase(false, true)]
	[TestCase(true, true)]
	public async Task CandleGatewayUsesReservedSlotAndMapsRequestAndResponse(bool dealer, bool weekend)
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		var from = clock.GetUtcNow().AddDays(-1);
		var invoker = new ScriptedInvoker((_, _) => new GetCandlesResponse
		{
			Candles =
			{
				new HistoricCandle
				{
					Time = Timestamp.FromDateTimeOffset(from), Open = new Quotation { Units = 11 }, Close = new Quotation { Units = 12 },
					High = new Quotation { Units = 14 }, Low = new Quotation { Units = 10 }, Volume = 7, IsComplete = true, CandleSource = CandleSource.Exchange,
				},
			},
		});
		using var gateway = new SdkTInvestGateway(invoker, new(), limiter, clock);
		using var reservation = await gateway.ReserveCandlesAsync(1, clock.GetUtcNow().AddSeconds(20), default);
		var rows = await gateway.GetCandlesAsync(new("invented-uid", from, clock.GetUtcNow(), 60, 2400, dealer, weekend), reservation, clock.GetUtcNow().AddSeconds(20), default);
		var request = (GetCandlesRequest)invoker.Requests.Single();
		Assert.That(request.InstrumentId, Is.EqualTo("invented-uid"));
		Assert.That(request.Interval, Is.EqualTo(CandleInterval._1Min));
		Assert.That(request.HasLimit, Is.False, "API 30220 forbids limit with candle_source_type; the planner bounds page duration.");
		Assert.That(request.CandleSourceType, Is.EqualTo(weekend ? GetCandlesRequest.Types.CandleSource.IncludeWeekend : GetCandlesRequest.Types.CandleSource.Exchange), "Dealer filtering does not select the weekend candle source.");
		Assert.That(rows.Single(), Is.EqualTo(CandleHistoryTests.Candle(from)));
		var available = 0;
		while (limiter.TryAcquire(TInvestLimitGroup.Users) == TimeSpan.Zero) available++;
		Assert.That(available, Is.EqualTo(44), "Reserved admission is consumed once, not twice.");
	}
	[TestCase(1, false)]
	[TestCase(2, false)]
	[TestCase(2, true)]
	public async Task CandleRetriesStayInsideCallCapAndEveryAttemptUsesSharedQuota(int cap, bool failsAgain)
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		var invoker = new ScriptedInvoker((_, n) => n == 1 || failsAgain
			? throw new RpcException(new Status(StatusCode.ResourceExhausted, "80002"), Reset(1)) : new GetCandlesResponse());
		using var gateway = new SdkTInvestGateway(invoker, new() { MaxCandleCallsPerRequest = cap }, limiter, clock);
		using var reservation = await gateway.ReserveCandlesAsync(1, clock.GetUtcNow().AddSeconds(20), default);
		var task = gateway.GetCandlesAsync(new("invented-uid", clock.GetUtcNow().AddHours(-1), clock.GetUtcNow(), 60, 2400, false), reservation, clock.GetUtcNow().AddSeconds(20), default);
		clock.Advance(TimeSpan.FromSeconds(1));
		if (cap == 1 || failsAgain)
		{
			var error = Assert.ThrowsAsync<TInvestRequestException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.That(error!.Kind, Is.EqualTo(TInvestFailureKind.Limit));
		}
		else
		{
			Assert.That(await task.WaitAsync(TimeSpan.FromSeconds(5)), Is.Empty);
		}
		Assert.That(invoker.Calls, Is.EqualTo(cap));
	}

	static Metadata Reset(int seconds) => new() { { "x-ratelimit-reset", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
	static SdkTInvestGateway NewGateway(CallInvoker invoker, ManualTimeProvider clock) => new(invoker, new TInvestOptions(), new TInvestRateLimiter(clock), clock);

	sealed class HangingHandler : HttpMessageHandler
	{
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int Requests { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests++;
			Entered.TrySetResult();
			return await new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
		}
	}

	sealed class CaptureHandler(string secret, string host, int grpcStatus = 0) : HttpMessageHandler
	{
		public int Requests { get; private set; }
		public bool CredentialsApplied { get; private set; }
		public bool EndpointCorrect { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests++;
			CredentialsApplied = request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == secret && request.Headers.GetValues("x-app-name").Single() == "SomeDataProvider";
			EndpointCorrect = request.RequestUri?.Scheme == "https" && request.RequestUri.Host == host;
			var content = new ByteArrayContent(new byte[5]); // Empty protobuf, uncompressed gRPC frame.
			content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
			var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = new Version(2, 0), Content = content };
			response.TrailingHeaders.Add("grpc-status", grpcStatus.ToString(System.Globalization.CultureInfo.InvariantCulture));
			return Task.FromResult(response);
		}
	}

	sealed class ScriptedInvoker(Func<string, int, object> script) : CallInvoker
	{
		public int Calls { get; private set; }
		public List<object> Requests { get; } = new();

		public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
		{
			Calls++;
			Requests.Add(request!);
			Task<TResponse> response;
			try { response = Task.FromResult((TResponse)script(method.Name, Calls)); }
			catch (RpcException exception) { response = Task.FromException<TResponse>(exception); }
			return new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
		}

		public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
		public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
	}
}
