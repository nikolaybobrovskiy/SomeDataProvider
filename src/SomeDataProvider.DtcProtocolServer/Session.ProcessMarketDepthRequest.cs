namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Protocol;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DtcProtocolServer.DtcProtocol;
using SomeDataProvider.DtcProtocolServer.Market;

partial class Session
{
	readonly Dictionary<uint, DepthSubscription> _depthSubscriptions = new();
	readonly HashSet<uint> _depthSnapshotRequests = new();

	async Task ProcessMarketDepthRequestAsync(IMessageDecoder decoder, IMessageEncoder encoder, CancellationToken ct)
	{
		var request = decoder.DecodeMarketDepthRequest();
		var symbolId = request.SymbolId;
		if (request.RequestAction == RequestActionEnum.UNSUBSCRIBE)
		{
			lock (_sessionLock)
			{
				if (!_closing)
				{
					RemoveDepthSubscription(symbolId);
				}
			}
			return;
		}

		var snapshotOnly = request.RequestAction == RequestActionEnum.SNAPSHOT;
		if (!snapshotOnly && request.RequestAction != RequestActionEnum.SUBSCRIBE)
		{
			lock (_sessionLock)
			{
				if (!_closing)
				{
					encoder.EncodeMarketDepthReject(symbolId, "Market depth request action is not supported.");
					SendResponse(encoder.GetEncodedMessage());
				}
			}
			return;
		}

		DepthSubscription? state = null;
		var snapshotReserved = false;
		var requestedSymbol = request.Symbol;
		var exchange = request.Exchange;
		if (snapshotOnly)
		{
			lock (_sessionLock)
			{
				if (_closing)
				{
					return;
				}
				if (_depthSubscriptions.ContainsKey(symbolId) || !_depthSnapshotRequests.Add(symbolId))
				{
					encoder.EncodeMarketDepthReject(symbolId, "Snapshot-only requests cannot share a SymbolID with an active depth subscription.");
					SendResponse(encoder.GetEncodedMessage());
					return;
				}
				snapshotReserved = true;
			}
		}

		if (!snapshotOnly)
		{
			lock (_sessionLock)
			{
				if (_closing)
				{
					return;
				}
				if (_depthSnapshotRequests.Contains(symbolId))
				{
					encoder.EncodeMarketDepthReject(symbolId, "A depth snapshot-only request is already in progress for this SymbolID.");
					SendResponse(encoder.GetEncodedMessage());
					return;
				}

				var previous = _depthSubscriptions.GetValueOrDefault(symbolId);
				previous?.Stop.Cancel();
				_marketQueue?.RemoveDepth(symbolId);
				state = new(CancellationTokenSource.CreateLinkedTokenSource(ct), previous);
				_depthSubscriptions[symbolId] = state;
				if (string.IsNullOrWhiteSpace(requestedSymbol) && string.IsNullOrWhiteSpace(exchange))
				{
					var marketSymbol = _marketSubscriptions.GetValueOrDefault(symbolId)?.Symbol;
					if (marketSymbol != null)
					{
						requestedSymbol = marketSymbol.Code;
						exchange = marketSymbol.Exchange;
					}
				}
			}
		}
		else if (string.IsNullOrWhiteSpace(requestedSymbol) && string.IsNullOrWhiteSpace(exchange))
		{
			lock (_sessionLock)
			{
				var marketSymbol = _marketSubscriptions.GetValueOrDefault(symbolId)?.Symbol;
				if (marketSymbol != null)
				{
					requestedSymbol = marketSymbol.Code;
					exchange = marketSymbol.Exchange;
				}
			}
		}

		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), _timeProvider);
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, state?.Token ?? CancellationToken.None, deadline.Token);
		var until = _timeProvider.GetUtcNow().AddSeconds(20);
		try
		{
			if (_onlyHistoryServer)
			{
				throw new TInvestRequestException("Market depth is not available on the history-only listener.");
			}
			if (request.NumLevels < 0)
			{
				throw new TInvestRequestException("Market depth level count cannot be negative.");
			}

			var route = await _symbolsStoreProvider.GetSymbolsStoreAsync(requestedSymbol, exchange, cancel.Token).AsTask().WaitAsync(cancel.Token).ConfigureAwait(false);
			var symbol = route.HasValue ? await route.Value.SymbolsStore.GetSymbolAsync(route.Value.SymbolCode, cancel.Token).AsTask().WaitAsync(cancel.Token).ConfigureAwait(false) : null;
			if (symbol is not TInvestSymbol tInvest || !tInvest.HasMarketDepth || SessionMarketHub is not { } hub)
			{
				throw new TInvestRequestException("Symbol is unknown or market depth is not supported.");
			}

			var levels = DepthBook.ClientLevels(request.NumLevels, hub.MaxDepth);
			if (snapshotOnly)
			{
				using var subscription = await hub.SubscribeAsync(tInvest, e => ReceiveDepthSnapshotOnly(symbolId, tInvest.Instrument.Short.Lot, e), until, cancel.Token, snapshotOnly: true, depthLevels: levels).ConfigureAwait(false);
				return;
			}

			state!.Levels = levels;
			var depthSubscription = await hub.SubscribeAsync(tInvest, e => ReceiveDepthEvent(symbolId, state!, tInvest.Instrument.Short.Lot, e), until, cancel.Token, depthLevels: levels).ConfigureAwait(false);
			lock (_sessionLock)
			{
				state.Previous?.Dispose();
				state.Previous = null;
				if (!_closing && _depthSubscriptions.GetValueOrDefault(symbolId) == state && !state.Stop.IsCancellationRequested)
				{
					state.Subscription = depthSubscription;
				}
				else
				{
					depthSubscription.Dispose();
				}
			}
		}
		catch (Exception ex)
		{
			lock (_sessionLock)
			{
				if (_closing || ct.IsCancellationRequested || state?.Stop.IsCancellationRequested == true || (state != null && _depthSubscriptions.GetValueOrDefault(symbolId) != state))
				{
					return;
				}

				var text = ex is TInvestRequestException bridge ? bridge.Message : ex is OperationCanceledException ? "T-Invest market depth is unavailable; retry later." : "T-Invest market depth is unavailable.";
				var starting = ex is TInvestRequestException { Kind: TInvestFailureKind.Starting };
				if (starting) _startupRejected.Add(symbolId);
				encoder.EncodeMarketDepthReject(symbolId, text);
				SendResponse(encoder.GetEncodedMessage());
				if (state != null)
				{
					RemoveDepthSubscription(symbolId);
				}

				if (starting) AnnounceFeedIfStarted(symbolId);
			}
		}
		finally
		{
			if (snapshotReserved)
			{
				lock (_sessionLock)
				{
					_depthSnapshotRequests.Remove(symbolId);
				}
			}
		}
	}

	void ReceiveDepthSnapshotOnly(uint symbolId, int lot, HubEvent message)
	{
		if (message is not HubDepthSnapshot snapshot)
		{
			return;
		}

		lock (_sessionLock)
		{
			if (_closing)
			{
				return;
			}

			_marketQueue ??= new(((Server)Server).MarketDataHub?.QueueLimit ?? 50_000);
			_marketQueue.EnqueueDepthSnapshot(symbolId, EncodeDepthSnapshot(symbolId, snapshot.Book, lot));
			PumpMarketData();
		}
	}

	void ReceiveDepthEvent(uint symbolId, DepthSubscription state, int lot, HubEvent message)
	{
		lock (_sessionLock)
		{
			if (_closing || _depthSubscriptions.GetValueOrDefault(symbolId) != state || state.Stop.IsCancellationRequested)
			{
				return;
			}

			if (message is HubFeedStatus feed)
			{
				if (!feed.Available)
				{
					state.WasUnavailable = true;
				}
				else if (state.WasUnavailable)
				{
					state.AwaitingResubscribe = true;
					state.WasUnavailable = false;
				}
				_marketQueue?.RemoveDepth(symbolId);
				state.Book = null;
				state.NeedsSnapshot = true;
				var statusEncoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
				statusEncoder.EncodeFeedStatus(symbolId, feed.Available);
				SendResponse(statusEncoder.GetEncodedMessage());
				return;
			}

			if (state.AwaitingResubscribe)
			{
				return;
			}

			_marketQueue ??= new(((Server)Server).MarketDataHub?.QueueLimit ?? 50_000);
			switch (message)
			{
				case HubDepthSnapshot snapshot:
				{
					var book = DepthBook.Limit(snapshot.Book, state.Levels);
					state.Book = book;
					state.NeedsSnapshot = false;
					_marketQueue.EnqueueDepthSnapshot(symbolId, EncodeDepthSnapshot(symbolId, book, lot));
					break;
				}
				case HubDepthUpdate update:
				{
					var book = DepthBook.Limit(update.Book, state.Levels);
					if (state.NeedsSnapshot || state.Book == null)
					{
						state.Book = book;
						state.NeedsSnapshot = false;
						_marketQueue.EnqueueDepthSnapshot(symbolId, EncodeDepthSnapshot(symbolId, book, lot));
						break;
					}

					var changes = DepthBook.Diff(state.Book, book, state.Levels);
					state.Book = book;
					if (changes.Length != 0)
					{
						var snapshotFrameCount = Math.Max(2, book.Bids.Count + book.Asks.Count + 1);
						_marketQueue.EnqueueDepthUpdate(symbolId, EncodeDepthUpdates(symbolId, update.Book.Time, changes, lot), snapshotFrameCount, () => EncodeDepthSnapshot(symbolId, book, lot));
					}
					break;
				}
			}
			PumpMarketData();
		}
	}

	IReadOnlyList<byte[]> EncodeDepthSnapshot(uint symbolId, TInvestOrderBook book, int lot)
	{
		var frames = new List<byte[]>(book.Bids.Count + book.Asks.Count + 1);
		var hasLevel = book.Bids.Count + book.Asks.Count != 0;
		if (!hasLevel)
		{
			AddSnapshotLevel(AtBidOrAskEnum8.BID_ASK_UNSET_8, 0, 0, 0, FinalUpdateInBatchEnum.FINAL_UPDATE_BEGIN_BATCH);
		}
		else
		{
			var first = true;
			AddLevels(AtBidOrAskEnum8.AT_BID_8, book.Bids, ref first);
			AddLevels(AtBidOrAskEnum8.AT_ASK_8, book.Asks, ref first);
		}
		AddSnapshotLevel(AtBidOrAskEnum8.BID_ASK_UNSET_8, 0, 0, 0, FinalUpdateInBatchEnum.FINAL_UPDATE_TRUE);
		return frames;

		void AddLevels(AtBidOrAskEnum8 side, IReadOnlyList<TInvestBookLevel> levels, ref bool first)
		{
			for (var i = 0; i < levels.Count; i++)
			{
				var level = levels[i];
				AddSnapshotLevel(side, (float)level.Price, (float)(level.Lots * (double)lot), checked((ushort)(i + 1)), first ? FinalUpdateInBatchEnum.FINAL_UPDATE_BEGIN_BATCH : FinalUpdateInBatchEnum.FINAL_UPDATE_FALSE);
				first = false;
			}
		}

		void AddSnapshotLevel(AtBidOrAskEnum8 side, float price, float quantity, ushort level, FinalUpdateInBatchEnum finalUpdate)
		{
			var encoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
			encoder.EncodeMarketDepthSnapshotLevel(symbolId, side, price, quantity, level, finalUpdate);
			frames.Add(encoder.GetEncodedMessage());
		}
	}

	IReadOnlyList<byte[]> EncodeDepthUpdates(uint symbolId, TInvestTimestamp time, IReadOnlyList<DepthBook.Change> changes, int lot)
	{
		var frames = new List<byte[]>(changes.Count);
		var milliseconds = checked(time.Seconds * 1_000 + time.Nanos / 1_000_000L);
		for (var i = 0; i < changes.Count; i++)
		{
			var change = changes[i];
			var finalUpdate = changes.Count == 1 || i == changes.Count - 1
				? FinalUpdateInBatchEnum.FINAL_UPDATE_TRUE
				: i == 0 ? FinalUpdateInBatchEnum.FINAL_UPDATE_BEGIN_BATCH : FinalUpdateInBatchEnum.FINAL_UPDATE_FALSE;
			var encoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
			encoder.EncodeMarketDepthUpdateLevel(symbolId, milliseconds, change.Side, change.Action, change.Price, change.Lots * (double)lot, change.Level, finalUpdate);
			frames.Add(encoder.GetEncodedMessage());
		}
		return frames;
	}

	void RemoveDepthSubscription(uint symbolId)
	{
		if (_depthSubscriptions.Remove(symbolId, out var old))
		{
			old.Dispose();
		}
		_marketQueue?.RemoveDepth(symbolId);
	}

	void StopDepthSubscriptions()
	{
		foreach (var state in _depthSubscriptions.Values)
		{
			state.Dispose();
		}
		_depthSubscriptions.Clear();
	}

	sealed class DepthSubscription : IDisposable
	{
		bool _disposed;
		public DepthSubscription(CancellationTokenSource stop, DepthSubscription? previous)
		{
			Stop = stop;
			Token = stop.Token;
			Previous = previous;
		}

		public CancellationTokenSource Stop { get; }
		public CancellationToken Token { get; }
		public DepthSubscription? Previous { get; set; }
		public TInvestMarketDataHub.Subscription? Subscription { get; set; }
		public int Levels { get; set; }
		public TInvestOrderBook? Book { get; set; }
		public bool NeedsSnapshot { get; set; } = true;
		public bool WasUnavailable { get; set; }
		public bool AwaitingResubscribe { get; set; }

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
