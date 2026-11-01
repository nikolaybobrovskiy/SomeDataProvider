namespace SomeDataProvider.DataStorage.TInvest;

using System.Runtime.CompilerServices;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Tinkoff.InvestApi.V1;

public sealed partial class SdkTInvestGateway
{
	public const int StreamPingMilliseconds = 10_000;
	public async Task<ITInvestMarketDataStream> OpenMarketDataStreamAsync(CancellationToken cancellationToken)
	{
		var deadline = _clock.GetUtcNow().AddSeconds(8);
		await _limiter.AcquireAsync(TInvestLimitGroup.Subscriptions, deadline, cancellationToken).ConfigureAwait(false);
		var client = new MarketDataStreamService.MarketDataStreamServiceClient(_marketInvoker);
		var call = client.MarketDataStream(cancellationToken: cancellationToken);
		var stream = new MarketStream(this, call);
		try
		{
			await stream.WriteAsync(new MarketDataRequest
			{
				PingSettings = new PingDelaySettings
				{
					PingDelayMs = StreamPingMilliseconds
				}
			}, deadline, cancellationToken).ConfigureAwait(false);
			return stream;
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}

	public async Task<TInvestMarketSnapshot> GetMarketSnapshotAsync(string uid, bool includeDealer, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		var request = new GetLastPricesRequest
		{
			LastPriceType = includeDealer ? LastPriceType.LastPriceUnspecified : LastPriceType.LastPriceExchange
		};
		request.InstrumentId.Add(uid);
		var prices = await CallAsync(TInvestLimitGroup.MarketData, () => _marketData.GetLastPricesAsync(request, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false);
		var book = await CallAsync(TInvestLimitGroup.MarketData, () => _marketData.GetOrderBookAsync(new GetOrderBookRequest
		{
			InstrumentId = uid,
			Depth = 1
		}, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false);
		var status = await CallAsync(TInvestLimitGroup.MarketData, () => _marketData.GetTradingStatusAsync(new GetTradingStatusRequest
		{
			InstrumentId = uid
		}, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false);
		var last = prices.LastPrices.Where(p => p.InstrumentUid == uid && (includeDealer || p.LastPriceType == LastPriceType.LastPriceExchange)).OrderBy(p => p.Time?.Seconds).ThenBy(p => p.Time?.Nanos).LastOrDefault();
		return new(last?.Price == null ? null : (double)Decimal(last.Price), Time(last?.Time), Quote(uid, book.OrderbookTs, book.Bids, book.Asks), Trading(status.TradingStatus));
	}

	static TInvestTimestamp? Time(Timestamp? time) => time == null ? null : new(time.Seconds, time.Nanos);
	static TInvestTradingStatus Trading(SecurityTradingStatus status) => System.Enum.IsDefined(typeof(TInvestTradingStatus), (int)status) ? (TInvestTradingStatus)(int)status : TInvestTradingStatus.Unspecified;
	static TInvestQuote Quote(string uid, Timestamp? time, IEnumerable<Order> bids, IEnumerable<Order> asks, bool consistent = true)
	{
		var bid = bids.FirstOrDefault();
		var ask = asks.FirstOrDefault();
		return new(uid, Time(time) ?? default, bid?.Price == null ? null : (double)Decimal(bid.Price), bid?.Quantity, ask?.Price == null ? null : (double)Decimal(ask.Price), ask?.Quantity, consistent);
	}

	Exception StreamFailure(RpcException ex)
	{
		LastFailureStatus = ex.StatusCode.ToString();
		LastFailureCode = System.Text.RegularExpressions.Regex.Match(_token?.Redact(ex.Status.Detail) ?? ex.Status.Detail, @"\b[0-9]{5}\b").Value;
		if (ex.StatusCode == StatusCode.ResourceExhausted)
		{
			Correct(TInvestLimitGroup.Subscriptions, ex.Trailers, true);
		}

		return new TInvestRequestException(ex.StatusCode == StatusCode.Unauthenticated ? "T-Invest token is missing, expired or for the other environment." : "T-Invest stream is unavailable.");
	}

	sealed class MarketStream : ITInvestMarketDataStream
	{
		readonly SdkTInvestGateway _owner;
		readonly AsyncDuplexStreamingCall<MarketDataRequest, MarketDataResponse> _call;
		public MarketStream(SdkTInvestGateway owner, AsyncDuplexStreamingCall<MarketDataRequest, MarketDataResponse> call)
		{
			_owner = owner;
			_call = call;
		}

		public async Task<IReadOnlyList<string>> SendAsync(TInvestStreamRequest request, DateTimeOffset deadline, CancellationToken cancellationToken)
		{
			await _owner._limiter.AcquireAsync(TInvestLimitGroup.Subscriptions, deadline, cancellationToken).ConfigureAwait(false);
			// A batch can wait behind the shared limiter. Withdraw expired/unsubscribed elements immediately before
			// serialization, rather than sending a stale captured batch after the client's deadline.
			var uids = request.Uids.Where(uid => request.IsWanted?.Invoke(uid) ?? true).ToArray();
			if (request.Kind != TInvestSubscriptionKind.All && uids.Length == 0)
			{
				return uids;
			}

			var action = request.Subscribe ? SubscriptionAction.Subscribe : SubscriptionAction.Unsubscribe;
			var message = new MarketDataRequest();
			switch (request.Kind)
			{
				case TInvestSubscriptionKind.Trades:
					message.SubscribeTradesRequest = new SubscribeTradesRequest
					{
						SubscriptionAction = action,
						TradeSource = _owner._includeDealer ? TradeSourceType.TradeSourceAll : TradeSourceType.TradeSourceExchange
					};
					message.SubscribeTradesRequest.Instruments.AddRange(uids.Select(uid => new TradeInstrument
					{
						InstrumentId = uid
					}));
					break;
				case TInvestSubscriptionKind.Book:
					message.SubscribeOrderBookRequest = new SubscribeOrderBookRequest
					{
						SubscriptionAction = action
					};
					message.SubscribeOrderBookRequest.Instruments.AddRange(uids.Select(uid => new OrderBookInstrument
					{
						InstrumentId = uid,
						Depth = 1,
						OrderBookType = OrderBookType.Exchange
					}));
					break;
				case TInvestSubscriptionKind.Info:
					message.SubscribeInfoRequest = new SubscribeInfoRequest
					{
						SubscriptionAction = action
					};
					message.SubscribeInfoRequest.Instruments.AddRange(uids.Select(uid => new InfoInstrument
					{
						InstrumentId = uid
					}));
					break;
				case TInvestSubscriptionKind.All:
					message.GetMySubscriptions = new GetMySubscriptions();
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(request));
			}

			await WriteAsync(message, deadline, cancellationToken).ConfigureAwait(false);
			return uids;
		}

		public async IAsyncEnumerable<TInvestMarketEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
		{
			while (await MoveNextAsync(cancellationToken).ConfigureAwait(false))
			{
				var response = _call.ResponseStream.Current;
				TInvestMarketEvent? item = response.PayloadCase switch
				{
					MarketDataResponse.PayloadOneofCase.Ping => new TInvestPing(),
					MarketDataResponse.PayloadOneofCase.Trade => new TInvestTrade(response.Trade.InstrumentUid, Time(response.Trade.Time) ?? default, (double)Decimal(response.Trade.Price), response.Trade.Quantity, response.Trade.Direction == TradeDirection.Buy ? TInvestDirection.Buy : response.Trade.Direction == TradeDirection.Sell ? TInvestDirection.Sell : TInvestDirection.Unknown, response.Trade.TradeSource == TradeSourceType.TradeSourceExchange),
					MarketDataResponse.PayloadOneofCase.Orderbook => Quote(response.Orderbook.InstrumentUid, response.Orderbook.Time, response.Orderbook.Bids, response.Orderbook.Asks, response.Orderbook.IsConsistent),
					MarketDataResponse.PayloadOneofCase.TradingStatus => new TInvestStatus(response.TradingStatus.InstrumentUid, Trading(response.TradingStatus.TradingStatus_)),
					MarketDataResponse.PayloadOneofCase.SubscribeTradesResponse => new TInvestSubscriptionReply(TInvestSubscriptionKind.Trades, response.SubscribeTradesResponse.TradeSubscriptions.Select(e => new TInvestSubscriptionElement(e.InstrumentUid, (TInvestSubscriptionStatus)(int)e.SubscriptionStatus)).ToArray()),
					MarketDataResponse.PayloadOneofCase.SubscribeOrderBookResponse => new TInvestSubscriptionReply(TInvestSubscriptionKind.Book, response.SubscribeOrderBookResponse.OrderBookSubscriptions.Select(e => new TInvestSubscriptionElement(e.InstrumentUid, (TInvestSubscriptionStatus)(int)e.SubscriptionStatus)).ToArray()),
					MarketDataResponse.PayloadOneofCase.SubscribeInfoResponse => new TInvestSubscriptionReply(TInvestSubscriptionKind.Info, response.SubscribeInfoResponse.InfoSubscriptions.Select(e => new TInvestSubscriptionElement(e.InstrumentUid, (TInvestSubscriptionStatus)(int)e.SubscriptionStatus)).ToArray()),
					_ => new TInvestOtherMessage(), // Other payloads prove liveness but are not pings.
				};
				yield return item;
			}
		}

		public void Dispose() => _call.Dispose();
		internal async Task WriteAsync(MarketDataRequest message, DateTimeOffset deadline, CancellationToken ct)
		{
			var remaining = deadline - _owner._clock.GetUtcNow();
			if (remaining <= TimeSpan.Zero)
			{
				throw new TInvestRequestException("T-Invest subscription limit reached; retry later.", TInvestFailureKind.Limit);
			}

			using var budget = new CancellationTokenSource(remaining, _owner._clock);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
			try
			{
				await _call.RequestStream.WriteAsync(message).WaitAsync(linked.Token).ConfigureAwait(false);
			}
			catch (RpcException ex)
			{
				throw _owner.StreamFailure(ex);
			}
		}

		async Task<bool> MoveNextAsync(CancellationToken ct)
		{
			try
			{
				return await _call.ResponseStream.MoveNext(ct).ConfigureAwait(false);
			}
			catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
			{
				throw new OperationCanceledException(ct);
			}
			catch (RpcException ex)
			{
				throw _owner.StreamFailure(ex);
			}
		}
	}
}
