namespace SomeDataProvider.DtcProtocolServer.Market;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SomeDataProvider.DataStorage.TInvest;

/// <summary>One server-wide actor owns references and current state. Slow sessions never block the upstream reader.</summary>
sealed class TInvestMarketDataHub : IDisposable
{
	readonly ITInvestGateway _gateway;
	readonly TInvestOptions _options;
	readonly TimeProvider _clock;
	readonly TInvestTickRecorder _recorder;
	readonly Func<double>? _jitter;
	readonly Channel<Action> _commands = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
	{
		SingleReader = true
	});
	readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
	readonly List<UpstreamStream> _streams = new();
	readonly CancellationTokenSource _stop = new();
	int _disposed;
	public TInvestMarketDataHub(ITInvestGateway gateway, TInvestOptions options, TimeProvider? clock = null, TInvestTickRecorder? recorder = null, Func<double>? jitter = null, bool persistRecording = true)
	{
		options.Validate();
		_gateway = gateway;
		_options = options;
		_clock = clock ?? TimeProvider.System;
		_jitter = jitter;
		_recorder = recorder ?? new TInvestTickRecorder(persistRecording ? TInvestTickRecorder.DefaultRoot : null, _clock);
		_recorder.RecordingFailed += () => Post(() => Diagnostic?.Invoke("Recorder stopped after an I/O failure or queue overflow."));
		Completion = Task.Run(RunAsync);
	}

	// Structural diagnostics only. Callbacks must not retain market payloads during an opt-in probe.
	internal event Action<TInvestMarketEvent>? Observed;
	internal event Action<string>? WatchListRetrying;
	internal event Action<string>? Diagnostic;
	public Task Completion { get; }
	public int QueueLimit => _options.SessionQueueLimit;

	public async Task<Subscription> SubscribeAsync(TInvestSymbol symbol, Action<HubEvent> receive, DateTimeOffset deadline, CancellationToken ct, bool snapshotOnly = false, bool recordingOnly = false)
	{
		if (symbol.Instrument.Short.Kind is TInvestInstrumentKind.StockOption or TInvestInstrumentKind.FuturesOption or TInvestInstrumentKind.IndexOption)
		{
			throw new TInvestRequestException("T-Invest options market data is not supported.", TInvestFailureKind.Unsupported);
		}

		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		var remaining = deadline - _clock.GetUtcNow();
		if (remaining <= TimeSpan.Zero)
		{
			throw Limited();
		}

		using var budget = new CancellationTokenSource(remaining, _clock);
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token, _stop.Token);
		var subscriber = new Subscriber(receive, snapshotOnly, deadline, cancel.Token, recordingOnly);
		var uid = symbol.Instrument.Short.Uid;
		if (snapshotOnly)
		{
			receive(new HubSnapshot(await GetSnapshotAsync(symbol, deadline, cancel.Token).ConfigureAwait(false)));
			return new Subscription(this, uid, subscriber);
		}

		var token = cancel.Token;
		using var registration = token.Register(() => Post(() =>
		{
			if (!subscriber.Active)
			{
				subscriber.Ready.TrySetCanceled(token);
				Remove(uid, subscriber);
			}
		}));
		Post(() => Add(symbol, subscriber));
		try
		{
			await subscriber.Ready.Task.ConfigureAwait(false);
			return new Subscription(this, uid, subscriber);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_stop.IsCancellationRequested)
		{
			Post(() => Remove(uid, subscriber));
			throw Limited();
		}
		catch
		{
			Post(() => Remove(uid, subscriber));
			throw;
		}
	}

	public Task StartWatchListAsync(TInvestStore store) => Task.WhenAll(_options.RecordSymbols.Select(name => WatchAsync(store, name)));
	public async Task<TInvestMarketSnapshot> GetSnapshotAsync(TInvestSymbol symbol, DateTimeOffset deadline, CancellationToken ct)
	{
		if (symbol.Instrument.Short.Kind is TInvestInstrumentKind.StockOption or TInvestInstrumentKind.FuturesOption or TInvestInstrumentKind.IndexOption)
		{
			throw new TInvestRequestException("T-Invest options market data is not supported.", TInvestFailureKind.Unsupported);
		}

		using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
		ct = stop.Token;
		var current = new TaskCompletionSource<TInvestMarketSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
		Post(() => current.TrySetResult(_entries.TryGetValue(symbol.Instrument.Short.Uid, out var entry) && entry.Live && entry.HasSnapshot ? entry.Snapshot : null));
		var snapshot = await current.Task.WaitAsync(ct).ConfigureAwait(false);
		return snapshot ?? await _gateway.GetMarketSnapshotAsync(symbol.Instrument.Short.Uid, _options.IncludeDealerTrades, deadline, ct).WaitAsync(ct).ConfigureAwait(false);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		_stop.Cancel();
		Post(() =>
		{
			foreach (var stream in _streams)
			{
				stream.Dispose();
			}

			foreach (var (uid, entry) in _entries)
			{
				_recorder.SetLive(uid, false);
				entry.Stop.Cancel();
				foreach (var subscriber in entry.Subscribers)
				{
					subscriber.Ready.TrySetCanceled();
				}
			}

			_entries.Clear();
			_recorder.Dispose();
		});
		_commands.Writer.TryComplete();
	}

	internal Task BarrierAsync()
	{
		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Post(() => done.TrySetResult());
		return done.Task;
	}

	static TInvestRequestException Limited() => new("T-Invest subscription limit reached; retry later.", TInvestFailureKind.Limit);
	async Task WatchAsync(TInvestStore store, string name)
	{
		while (!_stop.IsCancellationRequested)
		{
			var separator = name.LastIndexOf('-');
			try
			{
				var symbol = await store.ResolveAsync(name[..separator], name[(separator + 1)..], _clock.GetUtcNow().AddSeconds(8), _stop.Token).ConfigureAwait(false);
				if (symbol != null)
				{
					using var subscription = await SubscribeAsync(symbol, _ =>
					{
					}, _clock.GetUtcNow().AddSeconds(20), _stop.Token, recordingOnly: true).ConfigureAwait(false);
					await Task.Delay(Timeout.InfiniteTimeSpan, _clock, _stop.Token).ConfigureAwait(false);
					return;
				}
			}
			catch (TInvestRequestException)
			{ /* A failed startup subscription proves no coverage; retry it. */
			}
			catch (OperationCanceledException) when (_stop.IsCancellationRequested)
			{
				return;
			}

			try
			{
				WatchListRetrying?.Invoke(name);
				await Task.Delay(TimeSpan.FromSeconds(30), _clock, _stop.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (_stop.IsCancellationRequested)
			{
				return;
			}
		}
	}

	void Add(TInvestSymbol symbol, Subscriber subscriber)
	{
		if (_stop.IsCancellationRequested || subscriber.Cancellation.IsCancellationRequested || _clock.GetUtcNow() >= subscriber.Deadline)
		{
			subscriber.Ready.TrySetCanceled();
			return;
		}

		var uid = symbol.Instrument.Short.Uid;
		if (!_entries.TryGetValue(uid, out var entry))
		{
			var index = Enumerable.Range(0, _streams.Count).FirstOrDefault(i => _entries.Values.Count(e => e.Stream == i) < 150, -1);
			if (index < 0)
			{
				if (_streams.Count == _options.MaxStreams)
				{
					subscriber.Ready.TrySetException(new TInvestRequestException("Server subscription limit reached."));
					return;
				}

				index = _streams.Count;
				_streams.Add(new(_gateway, _clock, _options.StreamSilenceTimeout, e => Post(() => OnEvent(e)), (id, live) => Post(() => Availability(id, live)), (id, status) => Post(() => Reject(id, status)), _jitter, at => Post(() =>
				{
					foreach (var item in _entries.Where(e => e.Value.Stream == index))
					{
						_recorder.Confirm(item.Key, at);
					}
				}), text => Post(() => Diagnostic?.Invoke(text))));
			}

			_entries[uid] = entry = new(symbol, index);
			UpdateDesired(index);
		}

		entry.Subscribers.Add(subscriber);
		if (!subscriber.RecordingOnly && !entry.HasSnapshot && !entry.Bootstrapping)
		{
			StartSnapshot(uid, entry);
		}

		TryReady(uid, entry);
	}

	void StartSnapshot(string uid, Entry entry)
	{
		entry.Bootstrapping = true;
		var version = entry.Version;
		var statusVersion = entry.StatusVersion;
		_ = BootstrapAsync();
		async Task BootstrapAsync()
		{
			using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20), _clock);
			using var cancel = CancellationTokenSource.CreateLinkedTokenSource(entry.Stop.Token, budget.Token, _stop.Token);
			try
			{
				var snapshot = await _gateway.GetMarketSnapshotAsync(uid, _options.IncludeDealerTrades, _clock.GetUtcNow().AddSeconds(20), cancel.Token).ConfigureAwait(false);
				Post(() =>
				{
					if (!_entries.TryGetValue(uid, out var current) || current != entry || current.Version != version)
					{
						return;
					}

					var state = current.Snapshot;
					current.Snapshot = snapshot with
					{
						LastPrice = state.LastTime != null && (snapshot.LastTime == null || state.LastTime.Value.Microseconds >= snapshot.LastTime.Value.Microseconds) ? state.LastPrice : snapshot.LastPrice,
						LastTime = state.LastTime != null && (snapshot.LastTime == null || state.LastTime.Value.Microseconds >= snapshot.LastTime.Value.Microseconds) ? state.LastTime : snapshot.LastTime,
						LastLots = state.LastTime != null && (snapshot.LastTime == null || state.LastTime.Value.Microseconds >= snapshot.LastTime.Value.Microseconds) ? state.LastLots : snapshot.LastLots,
						Quote = state.Quote != null && (snapshot.Quote == null || state.Quote.Time.Microseconds >= snapshot.Quote.Time.Microseconds) ? state.Quote : snapshot.Quote,
						Status = current.StatusVersion != statusVersion ? state.Status : snapshot.Status,
					};
					current.HasSnapshot = true;
					current.Bootstrapping = false;
					TryReady(uid, current);
				});
			}
			catch (OperationCanceledException) when (_stop.IsCancellationRequested || entry.Stop.IsCancellationRequested)
			{
			}
			catch (Exception)
			{
				Post(() =>
				{
					if (!_entries.TryGetValue(uid, out var current) || current != entry || current.Version != version)
					{
						return;
					}

					current.Bootstrapping = false;
					foreach (var subscriber in current.Subscribers.Where(s => !s.Active && !s.RecordingOnly).ToArray())
					{
						subscriber.Ready.TrySetException(new TInvestRequestException("T-Invest subscription snapshot is unavailable; retry later."));
						Remove(uid, subscriber);
					}
				});
			}
		}
	}

	void TryReady(string uid, Entry entry)
	{
		if (!entry.Live)
		{
			return;
		}

		foreach (var subscriber in entry.Subscribers.Where(s => !s.Active && (entry.HasSnapshot || s.RecordingOnly)).ToArray())
		{
			if (subscriber.Cancellation.IsCancellationRequested || _clock.GetUtcNow() >= subscriber.Deadline)
			{
				subscriber.Ready.TrySetException(Limited());
				Remove(uid, subscriber);
				continue;
			}

			subscriber.Active = true;
			if (!subscriber.RecordingOnly)
			{
				subscriber.Receive(new HubSnapshot(entry.Snapshot));
			}

			subscriber.Ready.TrySetResult();
			if (subscriber.SnapshotOnly)
			{
				Remove(uid, subscriber);
			}
		}
	}

	void Remove(string uid, Subscriber subscriber)
	{
		if (!_entries.TryGetValue(uid, out var entry) || !entry.Subscribers.Remove(subscriber))
		{
			return;
		}

		if (entry.Subscribers.Count != 0)
		{
			return;
		}

		_recorder.SetLive(uid, false);
		entry.Stop.Cancel();
		_entries.Remove(uid);
		UpdateDesired(entry.Stream);
	}

	void UpdateDesired(int stream) => _streams[stream].SetDesired(_entries.Where(e => e.Value.Stream == stream).Select(e => e.Key));
	void Reject(string uid, TInvestSubscriptionStatus status)
	{
		Diagnostic?.Invoke("Subscription element rejected: " + status);
		if (!_entries.TryGetValue(uid, out var entry))
		{
			return;
		}

		var text = status switch
		{
			TInvestSubscriptionStatus.InstrumentNotFound => "Unknown instrument.",
			TInvestSubscriptionStatus.LimitIsExceeded => "Server subscription limit reached.",
			TInvestSubscriptionStatus.TooManyRequests => "T-Invest subscription limit reached; retry later.",
			_ => "T-Invest subscription rejected.",
		};
		foreach (var subscriber in entry.Subscribers.Where(s => !s.Active).ToArray())
		{
			subscriber.Ready.TrySetException(new TInvestRequestException(text));
			Remove(uid, subscriber);
		}

		Availability(uid, false);
	}

	void Availability(string uid, bool available)
	{
		if (!_entries.TryGetValue(uid, out var entry) || (entry.Live == available && entry.SeenAvailability))
		{
			return;
		}

		entry.SeenAvailability = true;
		Diagnostic?.Invoke(available ? "Symbol feed available." : "Symbol feed unavailable.");
		entry.Live = available;
		_recorder.SetLive(uid, available);
		if (!available)
		{
			entry.Version++;
			entry.HasSnapshot = false;
			entry.Bootstrapping = false;
			entry.Snapshot = new();
		}

		foreach (var subscriber in entry.Subscribers.Where(s => s.Active))
		{
			subscriber.Receive(new HubFeedStatus(available));
		}

		if (available)
		{
			if (entry.Subscribers.Any(s => !s.Active && !s.RecordingOnly) && !entry.HasSnapshot && !entry.Bootstrapping)
			{
				StartSnapshot(uid, entry);
			}

			TryReady(uid, entry);
		}
	}

	void OnEvent(TInvestMarketEvent item)
	{
		Observed?.Invoke(item);
		var uid = item switch
		{
			TInvestTrade t => t.Uid,
			TInvestQuote q => q.Uid,
			TInvestStatus s => s.Uid,
			_ => null
		};
		if (uid == null || !_entries.TryGetValue(uid, out var entry))
		{
			return;
		}

		if (item is TInvestTrade trade)
		{
			_recorder.Record(trade);
			if (!_options.IncludeDealerTrades && !trade.IsExchange)
			{
				return;
			}

			entry.Snapshot = entry.Snapshot with
			{
				LastPrice = trade.Price,
				LastTime = trade.Time,
				LastLots = trade.Lots
			};
		}
		else if (item is TInvestQuote quote)
		{
			if (!quote.Consistent || (entry.Snapshot.Quote != null && quote.Time.Microseconds < entry.Snapshot.Quote.Time.Microseconds))
			{
				return;
			}

			var old = entry.Snapshot.Quote;
			entry.Snapshot = entry.Snapshot with
			{
				Quote = quote
			};
			if (old != null && old.Bid == quote.Bid && old.Ask == quote.Ask && old.BidLots == quote.BidLots && old.AskLots == quote.AskLots)
			{
				return;
			}
		}
		else if (item is TInvestStatus status)
		{
			entry.StatusVersion++;
			entry.Snapshot = entry.Snapshot with
			{
				Status = status.Status
			};
		}

		if (entry.Live)
		{
			foreach (var subscriber in entry.Subscribers.Where(s => s.Active))
			{
				subscriber.Receive(new HubUpdate(item));
			}
		}
	}

	void Post(Action action) => _commands.Writer.TryWrite(action);
	async Task RunAsync()
	{
		await foreach (var action in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
		{
			try
			{
				action();
			}
			catch (Exception)
			{
				Diagnostic?.Invoke("Hub action failed; no source payload was logged.");
			}
		}
	}

	internal sealed class Subscriber
	{
		public Subscriber(Action<HubEvent> receive, bool snapshotOnly, DateTimeOffset deadline, CancellationToken cancellation, bool recordingOnly)
		{
			Receive = receive;
			SnapshotOnly = snapshotOnly;
			Deadline = deadline;
			Cancellation = cancellation;
			RecordingOnly = recordingOnly;
		}

		public DateTimeOffset Deadline { get; }
		public CancellationToken Cancellation { get; }
		public Action<HubEvent> Receive { get; }
		public bool SnapshotOnly { get; }
		public bool RecordingOnly { get; }
		public bool Active { get; set; }
		public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	internal sealed class Subscription : IDisposable
	{
		readonly TInvestMarketDataHub _hub;
		readonly string _uid;
		Subscriber? _subscriber;
		internal Subscription(TInvestMarketDataHub hub, string uid, Subscriber subscriber)
		{
			_hub = hub;
			_uid = uid;
			_subscriber = subscriber;
		}

		public void Dispose()
		{
			var subscriber = Interlocked.Exchange(ref _subscriber, null);
			if (subscriber != null)
			{
				_hub.Post(() => _hub.Remove(_uid, subscriber));
			}
		}
	}

	sealed class Entry
	{
		public Entry(TInvestSymbol symbol, int stream)
		{
			Symbol = symbol;
			Stream = stream;
		}

		public TInvestSymbol Symbol { get; }
		public int Stream { get; }
		public List<Subscriber> Subscribers { get; } = new();
		public CancellationTokenSource Stop { get; } = new();
		public TInvestMarketSnapshot Snapshot { get; set; } = new();
		public bool Live { get; set; }
		public bool SeenAvailability { get; set; }
		public bool HasSnapshot { get; set; }
		public bool Bootstrapping { get; set; }
		public int Version { get; set; }
		public int StatusVersion { get; set; }
	}
}
