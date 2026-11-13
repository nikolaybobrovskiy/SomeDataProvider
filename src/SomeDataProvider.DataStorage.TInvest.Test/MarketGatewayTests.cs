namespace SomeDataProvider.DataStorage.TInvest.Test;

using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;
using Tinkoff.InvestApi.V1;

[TestFixture]
public class MarketGatewayTests
{
	[Test]
	public async Task ProbeHubsCannotRaiseTheNormalGatewayStreamCeiling()
	{
		var clock = new ManualTimeProvider();
		var invoker = new Invoker();
		using var gateway = new SdkTInvestGateway(invoker, new TInvestOptions { MaxStreams = 1 }, new TInvestRateLimiter(clock), clock);
		using var user = await gateway.OpenMarketDataStreamAsync(default);
		using var scopes = new SomeDataProvider.DataStorage.Definitions.ProbeScopeRegistry(clock);
		using (SomeDataProvider.DataStorage.Definitions.ProbeContext.Enter(scopes.Open("synthetic-probe", 180, 2, 0)))
			Assert.ThrowsAsync<TInvestRequestException>(() => gateway.OpenMarketDataStreamAsync(default));
		Assert.That(invoker.Opens, Is.EqualTo(1));
		await user.SendAsync(new(TInvestSubscriptionKind.Trades, new[] { "invented-uid" }), clock.GetUtcNow().AddSeconds(20), default);
		user.Dispose();
		user.Dispose();
		using var replacement = await gateway.OpenMarketDataStreamAsync(default);
		Assert.That(invoker.Opens, Is.EqualTo(2));
	}

	[Test]
	public async Task StreamSetupAndEachSubscriptionDispatchConsumeTheProbeBudget()
	{
		var clock = new ManualTimeProvider();
		using var scopes = new SomeDataProvider.DataStorage.Definitions.ProbeScopeRegistry(clock);
		var scope = scopes.Open("synthetic-probe", 180, 2, 0);
		var invoker = new Invoker();
		using var gateway = new SdkTInvestGateway(invoker, new TInvestOptions(), new TInvestRateLimiter(clock), clock);
		using (SomeDataProvider.DataStorage.Definitions.ProbeContext.Enter(scope))
		{
			using var stream = await gateway.OpenMarketDataStreamAsync(default);
			await stream.SendAsync(new(TInvestSubscriptionKind.Trades, new[] { "invented-uid" }), clock.GetUtcNow().AddSeconds(20), default);
			Assert.ThrowsAsync<SomeDataProvider.DataStorage.Definitions.ProbeRejectedException>(() => stream.SendAsync(new(TInvestSubscriptionKind.Info, new[] { "invented-uid" }), clock.GetUtcNow().AddSeconds(20), default));
		}
		Assert.That(invoker.Opens, Is.EqualTo(1));
		Assert.That(invoker.Writer.Messages.Count, Is.EqualTo(2), "Ping setup and one subscription only.");
		Assert.DoesNotThrowAsync(async () => { using var user = await gateway.OpenMarketDataStreamAsync(default); });
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task StreamUsesDirectInvokerSourceSettingsAndNormalizedEvents(bool dealer)
	{
		var clock = new ManualTimeProvider();
		var invoker = new Invoker();
		using var gateway = new SdkTInvestGateway(invoker, new TInvestOptions
		{
			IncludeDealerTrades = dealer
		}, new TInvestRateLimiter(clock), clock);
		using var stream = await gateway.OpenMarketDataStreamAsync(default);
		foreach (var kind in new[]
		{
			TInvestSubscriptionKind.Trades,
			TInvestSubscriptionKind.Book,
			TInvestSubscriptionKind.Info,
			TInvestSubscriptionKind.All
		}

		)
		{
			await stream.SendAsync(new(kind, new[]
			{
				"invented-uid"
			}), clock.GetUtcNow().AddSeconds(20), default);
		}

		Assert.That(invoker.Opens, Is.EqualTo(1));
		Assert.That(invoker.Writer.Messages[0].PingSettings.PingDelayMs, Is.EqualTo(SdkTInvestGateway.StreamPingMilliseconds));
		Assert.That(invoker.Writer.Messages[1].SubscribeTradesRequest.TradeSource, Is.EqualTo(dealer ? TradeSourceType.TradeSourceAll : TradeSourceType.TradeSourceExchange));
		Assert.That(invoker.Writer.Messages[2].SubscribeOrderBookRequest.Instruments.Single().OrderBookType, Is.EqualTo(OrderBookType.Exchange));
		Assert.That(invoker.Writer.Messages[2].SubscribeOrderBookRequest.Instruments.Single().Depth, Is.EqualTo(1));
		invoker.Reader.Emit(new MarketDataResponse
		{
			Trade = new Trade
			{
				InstrumentUid = "invented-uid",
				Time = new Timestamp
				{
					Seconds = 100,
					Nanos = 123456789
				},
				Price = new Quotation
				{
					Units = 12,
					Nano = 500000000
				},
				Quantity = 3,
				Direction = TradeDirection.Sell,
				TradeSource = TradeSourceType.TradeSourceExchange
			}
		});
		await using var events = stream.ReadAllAsync(default).GetAsyncEnumerator();
		Assert.That(await events.MoveNextAsync(), Is.True);
		Assert.That(events.Current, Is.EqualTo(new TInvestTrade("invented-uid", new(100, 123456789), 12.5, 3, TInvestDirection.Sell, true)));
		invoker.Reader.Emit(new MarketDataResponse
		{
			TradingStatus = new TradingStatus
			{
				InstrumentUid = "invented-uid",
				TradingStatus_ = SecurityTradingStatus.DealerNotAvailableForTrading
			}
		});
		Assert.That(await events.MoveNextAsync(), Is.True);
		Assert.That(((TInvestStatus)events.Current).Status, Is.EqualTo(TInvestTradingStatus.DealerNotAvailableForTrading));
	}

	[Test]
	public async Task BatchWithdrawalIsRecheckedAfterWaitingForTheSharedAddressSlot()
	{
		var clock = new ManualTimeProvider();
		var limiter = new TInvestRateLimiter(clock);
		var invoker = new Invoker();
		using var gateway = new SdkTInvestGateway(invoker, new TInvestOptions(), limiter, clock);
		using var stream = await gateway.OpenMarketDataStreamAsync(default);
		Assert.That(limiter.InspectTraffic().Address.Count, Is.EqualTo(1), "Stream setup books one slot, not a second slot for ping settings.");
		while (limiter.TryAcquire(TInvestLimitGroup.Users) == TimeSpan.Zero)
		{
		}

		var wanted = true;
		var send = stream.SendAsync(new(TInvestSubscriptionKind.Trades, new[]
		{
			"invented-uid"
		}, true, _ => wanted), clock.GetUtcNow().AddSeconds(20), default);
		Assert.That(send.IsCompleted, Is.False);
		wanted = false;
		clock.Advance(TimeSpan.FromSeconds(1));
		Assert.That(await send.WaitAsync(TimeSpan.FromSeconds(5)), Is.Empty);
		Assert.That(invoker.Writer.Messages.Count, Is.EqualTo(1), "Only ping settings were written; the withdrawn subscription never went upstream.");
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task QuietSnapshotUsesThreeBudgetedUnaryCallsAndFiltersDealerLastPrices(bool dealer)
	{
		var clock = new ManualTimeProvider();
		var invoker = new Invoker();
		var limiter = new TInvestRateLimiter(clock);
		using var gateway = new SdkTInvestGateway(invoker, new TInvestOptions(), limiter, clock);
		var snapshot = await gateway.GetMarketSnapshotAsync("invented-uid", dealer, clock.GetUtcNow().AddSeconds(20), default);
		Assert.That(invoker.UnaryNames, Is.EqualTo(new[]
		{
			"GetLastPrices",
			"GetOrderBook",
			"GetTradingStatus"
		}));
		Assert.That(snapshot.LastPrice, Is.EqualTo(dealer ? 13 : 12));
		Assert.That(snapshot.Quote!.Bid, Is.EqualTo(11));
		Assert.That(snapshot.Quote.Ask, Is.Null);
		Assert.That(snapshot.Status, Is.EqualTo(TInvestTradingStatus.SessionClose));
		var slots = 0;
		while (limiter.TryAcquire(TInvestLimitGroup.Users) == TimeSpan.Zero)
		{
			slots++;
		}

		Assert.That(slots, Is.EqualTo(45 - 3));
	}

	[Test]
	public void EveryPinnedSdkStatusHasAnExactBridgeCounterpart()
	{
		foreach (var status in System.Enum.GetValues<SecurityTradingStatus>())
		{
			Assert.That((int)System.Enum.Parse<TInvestTradingStatus>(status.ToString()), Is.EqualTo((int)status));
		}

		foreach (var status in System.Enum.GetValues<SubscriptionStatus>())
		{
			Assert.That((int)System.Enum.Parse<TInvestSubscriptionStatus>(status.ToString()), Is.EqualTo((int)status));
		}
	}

	sealed class Invoker : CallInvoker
	{
		public Writer Writer { get; } = new();
		public Reader Reader { get; } = new();
		public int Opens { get; private set; }
		public List<string> UnaryNames { get; } = new();

		public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
		{
			Opens++;
			return new((IClientStreamWriter<TRequest>)(object)Writer, (IAsyncStreamReader<TResponse>)(object)Reader, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => Reader.End());
		}

		public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
		{
			UnaryNames.Add(method.Name);
			object response = method.Name switch
			{
				"GetLastPrices" => new GetLastPricesResponse
				{
					LastPrices =
					{
						new LastPrice
						{
							InstrumentUid = "invented-uid",
							Price = new Quotation
							{
								Units = 12
							},
							Time = new Timestamp
							{
								Seconds = 1
							},
							LastPriceType = LastPriceType.LastPriceExchange
						},
						new LastPrice
						{
							InstrumentUid = "invented-uid",
							Price = new Quotation
							{
								Units = 13
							},
							Time = new Timestamp
							{
								Seconds = 2
							},
							LastPriceType = LastPriceType.LastPriceDealer
						}
					}
				},
				"GetOrderBook" => new GetOrderBookResponse
				{
					Bids =
					{
						new Order
						{
							Price = new Quotation
							{
								Units = 11
							},
							Quantity = 2
						}
					},
					OrderbookTs = new Timestamp
					{
						Seconds = 2
					}
				},
				"GetTradingStatus" => new GetTradingStatusResponse
				{
					TradingStatus = SecurityTradingStatus.SessionClose
				},
				_ => throw new InvalidOperationException("Unexpected scripted unary call."),
			};
			return new(Task.FromResult((TResponse)response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () =>
			{
			});
		}

		public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
		public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
	}

	sealed class Writer : IClientStreamWriter<MarketDataRequest>
	{
		public WriteOptions? WriteOptions { get; set; }
		public List<MarketDataRequest> Messages { get; } = new();

		public Task WriteAsync(MarketDataRequest message)
		{
			Messages.Add(message);
			return Task.CompletedTask;
		}

		public Task CompleteAsync() => Task.CompletedTask;
	}

	sealed class Reader : IAsyncStreamReader<MarketDataResponse>
	{
		readonly Channel<MarketDataResponse> _events = Channel.CreateUnbounded<MarketDataResponse>();
		public MarketDataResponse Current { get; private set; } = new();

		public void Emit(MarketDataResponse message) => _events.Writer.TryWrite(message);
		public void End() => _events.Writer.TryComplete();
		public async Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			if (!await _events.Reader.WaitToReadAsync(cancellationToken))
			{
				return false;
			}

			Current = await _events.Reader.ReadAsync(cancellationToken);
			return true;
		}
	}
}
