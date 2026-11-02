namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Protocol;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DtcProtocolServer.DtcProtocol;
using SomeDataProvider.DtcProtocolServer.Market;

partial class Session
{
	readonly Dictionary<uint, MarketSubscription> _marketSubscriptions = new();
	SessionMarketDataQueue? _marketQueue;
	bool _marketSending;
	async Task ProcessMarketDataRequestAsync(IMessageDecoder decoder, IMessageEncoder encoder, CancellationToken ct)
	{
		var request = decoder.DecodeMarketDataRequest();
		var symbolId = request.SymbolId;
		if (request.RequestAction == RequestActionEnum.SNAPSHOT)
		{
			await ProcessMarketSnapshotAsync(request.Symbol, request.Exchange, symbolId, encoder, ct).ConfigureAwait(false);
			return;
		}

		MarketSubscription state;
		lock (_sessionLock)
		{
			if (_closing)
			{
				return;
			}

			if (request.RequestAction == RequestActionEnum.UNSUBSCRIBE)
			{
				RemoveMarketSubscription(symbolId);
				return;
			}

			var previous = _marketSubscriptions.GetValueOrDefault(symbolId);
			previous?.Stop.Cancel();
			_marketQueue?.RemoveMarketData(symbolId);
			state = new(CancellationTokenSource.CreateLinkedTokenSource(ct), previous);
			_marketSubscriptions[symbolId] = state;
		}

		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), _timeProvider);
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(state.Token, deadline.Token);
		var until = _timeProvider.GetUtcNow().AddSeconds(20);
		try
		{
			if (request.RequestAction is not (RequestActionEnum.SUBSCRIBE or RequestActionEnum.SNAPSHOT))
			{
				throw new TInvestRequestException("Market data request action is not supported.");
			}

			if (_onlyHistoryServer)
			{
				throw new TInvestRequestException("Market data is not available on the history-only listener.");
			}

			var route = await _symbolsStoreProvider.GetSymbolsStoreAsync(request.Symbol, request.Exchange, cancel.Token).AsTask().WaitAsync(cancel.Token).ConfigureAwait(false);
			var symbol = route.HasValue ? await route.Value.SymbolsStore.GetSymbolAsync(route.Value.SymbolCode, cancel.Token).AsTask().WaitAsync(cancel.Token).ConfigureAwait(false) : null;
			if (symbol == null)
			{
				throw new TInvestRequestException($"Symbol is unknown or T-Invest source is not configured: {request.Symbol}.");
			}

			if (symbol is not TInvestSymbol tInvest || ((Server)Server).MarketDataHub is not { } hub)
			{
				throw new TInvestRequestException($"Real-time market data is not supported for {request.Symbol}.");
			}

			state.Symbol = tInvest;
			var snapshotOnly = request.RequestAction == RequestActionEnum.SNAPSHOT;
			var subscription = await hub.SubscribeAsync(tInvest, e => ReceiveMarketEvent(symbolId, state, tInvest.Instrument.Short.Lot, e), until, cancel.Token, snapshotOnly).ConfigureAwait(false);
			lock (_sessionLock)
			{
				state.Previous?.Dispose();
				state.Previous = null;
				if (!_closing && _marketSubscriptions.TryGetValue(symbolId, out var current) && current == state && !snapshotOnly)
				{
					state.Subscription = subscription;
				}
				else
				{
					subscription.Dispose();
				}

				if (snapshotOnly && _marketSubscriptions.GetValueOrDefault(symbolId) == state)
				{
					RemoveMarketSubscription(symbolId);
				}
			}
		}
		catch (Exception ex)
		{
			lock (_sessionLock)
			{
				if (_closing || state.Answered || state.Stop.IsCancellationRequested || _marketSubscriptions.GetValueOrDefault(symbolId) != state)
				{
					return;
				}

				var text = ex is TInvestRequestException bridge ? bridge.Message : ex is OperationCanceledException ? "T-Invest subscription limit reached; retry later." : "T-Invest market data is unavailable.";
				encoder.EncodeMarketDataReject(symbolId, text);
				SendResponse(encoder.GetEncodedMessage());
				RemoveMarketSubscription(symbolId);
			}
		}
	}

	async Task ProcessMarketSnapshotAsync(string requestedSymbol, string exchange, uint symbolId, IMessageEncoder encoder, CancellationToken ct)
	{
		using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20), _timeProvider);
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
		var until = _timeProvider.GetUtcNow().AddSeconds(20);
		try
		{
			if (_onlyHistoryServer)
			{
				throw new TInvestRequestException("Market data is not available on the history-only listener.");
			}

			var route = await _symbolsStoreProvider.GetSymbolsStoreAsync(requestedSymbol, exchange, cancel.Token).AsTask().WaitAsync(cancel.Token).ConfigureAwait(false);
			var symbol = route.HasValue ? await route.Value.SymbolsStore.GetSymbolAsync(route.Value.SymbolCode, cancel.Token).AsTask().WaitAsync(cancel.Token).ConfigureAwait(false) : null;
			if (symbol is not TInvestSymbol tInvest || ((Server)Server).MarketDataHub is not { } hub)
			{
				throw new TInvestRequestException("Symbol is unknown or real-time market data is not supported.");
			}

			var snapshot = await hub.GetSnapshotAsync(tInvest, until, cancel.Token).ConfigureAwait(false);
			lock (_sessionLock)
			{
				if (_closing)
				{
					return;
				}

				cancel.Token.ThrowIfCancellationRequested();
				encoder.EncodeTInvestSnapshot(symbolId, snapshot, tInvest.Instrument.Short.Lot);
				SendResponse(encoder.GetEncodedMessage());
			}
		}
		catch (Exception ex)
		{
			lock (_sessionLock)
			{
				if (_closing || ct.IsCancellationRequested)
				{
					return;
				}

				encoder.EncodeMarketDataReject(symbolId, ex is TInvestRequestException bridge ? bridge.Message : "T-Invest snapshot is unavailable; retry later.");
				SendResponse(encoder.GetEncodedMessage());
			}
		}
	}

	void ReceiveMarketEvent(uint symbolId, MarketSubscription state, int lot, HubEvent message)
	{
		lock (_sessionLock)
		{
			if (_closing || _marketSubscriptions.GetValueOrDefault(symbolId) != state || state.Stop.IsCancellationRequested)
			{
				return;
			}

			var encoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
			switch (message)
			{
				case HubSnapshot snapshot:
					encoder.EncodeTInvestSnapshot(symbolId, snapshot.Snapshot, lot);
					SendResponse(encoder.GetEncodedMessage());
					state.Answered = true;
					break;
				case HubFeedStatus feed:
					_marketQueue?.RemoveMarketData(symbolId);
					encoder.EncodeFeedStatus(symbolId, feed.Available);
					SendResponse(encoder.GetEncodedMessage());
					break;
				case HubUpdate update:
					encoder.EncodeTInvestUpdate(symbolId, update.Update, lot);
					if (update.Update is TInvestStatus)
					{
						SendResponse(encoder.GetEncodedMessage());
					}
					else
					{
						_marketQueue ??= new(((Server)Server).MarketDataHub?.QueueLimit ?? 50_000);
						_marketQueue.Enqueue(symbolId, encoder.GetEncodedMessage(), update.Update is TInvestQuote);
						PumpMarketData();
					}

					break;
			}
		}
	}

	void MarketSendCompleted()
	{
		lock (_sessionLock)
		{
			_marketSending = false;
			PumpMarketData();
		}
	}

	void PumpMarketData()
	{
		if (_closing || _marketSending || BytesPending != 0 || BytesSending != 0)
		{
			return;
		}

		var bytes = _marketQueue?.Dequeue();
		if (bytes == null)
		{
			return;
		}

		_marketSending = true;
		if (!SendResponse(bytes))
		{
			_marketSending = false;
		}
	}

	void RemoveMarketSubscription(uint symbolId)
	{
		if (_marketSubscriptions.Remove(symbolId, out var old))
		{
			old.Dispose();
		}

		_marketQueue?.RemoveMarketData(symbolId);
	}

	void StopMarketSubscriptions()
	{
		foreach (var state in _marketSubscriptions.Values)
		{
			state.Dispose();
		}

		_marketSubscriptions.Clear();
		StopDepthSubscriptions();
		_marketQueue?.Clear();
	}

	sealed class MarketSubscription : IDisposable
	{
		bool _disposed;
		public MarketSubscription(CancellationTokenSource stop, MarketSubscription? previous)
		{
			Stop = stop;
			Token = stop.Token;
			Previous = previous;
		}

		public CancellationTokenSource Stop { get; }
		public CancellationToken Token { get; }
		public bool Answered { get; set; }
		public TInvestSymbol? Symbol { get; set; }
		public MarketSubscription? Previous { get; set; }
		public TInvestMarketDataHub.Subscription? Subscription { get; set; }

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			Stop.Cancel();
			Stop.Dispose();
			Subscription?.Dispose();
			Previous?.Dispose();
			Previous = null;
		}
	}
}
