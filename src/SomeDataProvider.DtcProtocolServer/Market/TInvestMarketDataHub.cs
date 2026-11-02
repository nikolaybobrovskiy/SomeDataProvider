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
	public int MaxDepth => _options.MaxDepth;

	public async Task<Subscription> SubscribeAsync(TInvestSymbol symbol, Action<HubEvent> receive, DateTimeOffset deadline, CancellationToken ct, bool snapshotOnly = false, bool recordingOnly = false, int depthLevels = 0)
	{
		if (symbol.Instrument.Short.Kind is TInvestInstrumentKind.StockOption or TInvestInstrumentKind.FuturesOption or TInvestInstrumentKind.IndexOption)
		{
			throw new TInvestRequestException("T-Invest options market data is not supported.", TInvestFailureKind.Unsupported);
		}

		if (depthLevels < 0 || depthLevels > _options.MaxDepth)
		{
			throw new ArgumentOutOfRangeException(nameof(depthLevels));
		}

		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		var remaining = deadline - _clock.GetUtcNow();
		if (remaining <= TimeSpan.Zero)
		{
			throw Limited();
		}

		using var budget = new CancellationTokenSource(remaining, _clock);
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token, _stop.Token);
		var subscriber = new Subscriber(receive, snapshotOnly, deadline, cancel.Token, recordingOnly, depthLevels);
		var uid = symbol.Instrument.Short.Uid;
		if (snapshotOnly)
		{
			if (depthLevels > 0)
			{
				var upstreamDepth = DepthBook.UpstreamLevels(depthLevels, _options.MaxDepth);
				var current = new TaskCompletionSource<TInvestOrderBook?>(TaskCreationOptions.RunContinuationsAsynchronously);
				Post(() => current.TrySetResult(_entries.TryGetValue(uid, out var entry) && entry.Live && entry.HasBook && entry.Book is { } book &&
					entry.AvailableBookDepth >= upstreamDepth && book.Depth >= upstreamDepth && !entry.BookResyncPending &&
					book.IsConsistent && DepthBook.IsStructurallyConsistent(book) ? book : null));
				var book = await current.Task.WaitAsync(cancel.Token).ConfigureAwait(false)
					?? await GetConsistentOrderBookAsync(uid, upstreamDepth, deadline, cancel.Token, null).ConfigureAwait(false);
				receive(new HubDepthSnapshot(DepthBook.Limit(book, depthLevels), depthLevels));
			}
			else
			{
				receive(new HubSnapshot(await GetSnapshotAsync(symbol, deadline, cancel.Token).ConfigureAwait(false)));
			}
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
		var created = false;
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
				_streams.Add(new(_gateway, _clock, _options.StreamSilenceTimeout, e => Post(() => OnEvent(e)), (id, live) => Post(() => Availability(id, live)), (_, _) =>
				{
				}, _jitter, at => Post(() =>
				{
					foreach (var item in _entries.Where(e => e.Value.Stream == index))
					{
						_recorder.Confirm(item.Key, at);
					}
				}), text => Post(() => Diagnostic?.Invoke(text)), (id, kind, status) => Post(() => Reject(id, kind, status)),
					(id, depth) => Post(() => BookDepthReady(id, depth))));
			}

			_entries[uid] = entry = new(symbol, index);
			created = true;
		}

		entry.Subscribers.Add(subscriber);
		var desiredDepth = RequiredDepth(entry);
		if (created || desiredDepth != entry.UpstreamDepth)
		{
			if (desiredDepth != entry.UpstreamDepth)
			{
				if (desiredDepth < entry.UpstreamDepth && entry.Book is { } currentBook && currentBook.Depth > desiredDepth)
				{
					entry.Book = DepthBook.Limit(currentBook with { Depth = desiredDepth }, desiredDepth);
				}

				entry.UpstreamDepth = desiredDepth;
				entry.BookVersion++;
				entry.PendingBook = null;
				entry.BookBootstrapping = false;
			}
			UpdateDesired(entry.Stream);
		}

		if (!subscriber.RecordingOnly && subscriber.DepthLevels == 0 && !entry.HasSnapshot && !entry.Bootstrapping)
		{
			StartSnapshot(uid, entry);
		}
		if (subscriber.DepthLevels > 0 && NeedsDepthBootstrap(entry))
		{
			StartDepthSnapshot(uid, entry);
		}

		TryReady(uid, entry);
	}

	int RequiredDepth(Entry entry)
	{
		var requested = entry.Subscribers.Where(s => s.DepthLevels > 0).Select(s => s.DepthLevels).DefaultIfEmpty(1).Max();
		return DepthBook.UpstreamLevels(requested, _options.MaxDepth);
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

	bool NeedsDepthBootstrap(Entry entry) => !entry.BookBootstrapping &&
		(entry.BookResyncPending || entry.Book == null || entry.Book.Depth < entry.UpstreamDepth) &&
		entry.PendingBook?.Depth != entry.UpstreamDepth;

	async Task<TInvestOrderBook> GetConsistentOrderBookAsync(string uid, int depth, DateTimeOffset deadline, CancellationToken ct, TInvestTimestamp? fence)
	{
		Exception? failure = null;
		for (var attempt = 0; attempt < 3; attempt++)
		{
			ct.ThrowIfCancellationRequested();
			if (_clock.GetUtcNow() >= deadline)
			{
				throw failure ?? Limited();
			}

			try
			{
				var candidate = await _gateway.GetOrderBookAsync(uid, depth, deadline, ct).ConfigureAwait(false);
				if (candidate.Depth >= depth && string.Equals(candidate.Uid, uid, StringComparison.Ordinal) && candidate.IsConsistent && DepthBook.IsStructurallyConsistent(candidate) &&
					(fence == null || !DepthBook.IsOlder(candidate.Time, fence.Value)))
				{
					return DepthBook.Limit(candidate with { Depth = depth }, depth);
				}

				failure = new TInvestRequestException(fence != null && DepthBook.IsOlder(candidate.Time, fence.Value)
					? "T-Invest returned a stale order-book snapshot."
					: candidate.Depth < depth
						? "T-Invest returned a shallower order-book snapshot than requested."
						: "T-Invest returned an inconsistent order-book snapshot.");
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (TInvestRequestException ex) when (ex.Kind != TInvestFailureKind.Source)
			{
				failure = ex;
				break;
			}
			catch (Exception ex)
			{
				failure = ex;
			}

			if (attempt < 2)
			{
				var delay = TimeSpan.FromMilliseconds(100 * (attempt + 1));
				var remaining = deadline - _clock.GetUtcNow();
				if (remaining <= TimeSpan.Zero)
				{
					break;
				}

				await Task.Delay(delay <= remaining ? delay : remaining, _clock, ct).ConfigureAwait(false);
			}
		}

		throw failure ?? new TInvestRequestException("T-Invest market-depth snapshot is unavailable; retry later.");
	}

	void StartDepthSnapshot(string uid, Entry entry)
	{
		if (!entry.Subscribers.Any(s => s.DepthLevels > 0) || entry.BookBootstrapping || entry.PendingBook?.Depth == entry.UpstreamDepth)
		{
			return;
		}

		entry.BookBootstrapping = true;
		var version = entry.BookVersion;
		var depth = entry.UpstreamDepth;
		var deadline = _clock.GetUtcNow().AddSeconds(20);
		var fence = entry.BookFence;
		_ = BootstrapAsync();
		async Task BootstrapAsync()
		{
			using var budget = new CancellationTokenSource(deadline - _clock.GetUtcNow(), _clock);
			using var cancel = CancellationTokenSource.CreateLinkedTokenSource(entry.Stop.Token, budget.Token, _stop.Token);
			try
			{
				var book = await GetConsistentOrderBookAsync(uid, depth, deadline, cancel.Token, fence).ConfigureAwait(false);
				Post(() =>
				{
					if (!_entries.TryGetValue(uid, out var current) || current != entry || current.BookVersion != version)
					{
						return;
					}

					current.BookBootstrapping = false;
					if ((current.BookFence is { } currentFence && DepthBook.IsOlder(book.Time, currentFence)) ||
						(current.Book != null && DepthBook.IsOlder(book.Time, current.Book.Time)) ||
						(current.PendingBook != null && DepthBook.IsOlder(book.Time, current.PendingBook.Time)))
					{
						if (current.PendingBook?.Depth != depth)
						{
							current.PendingBook = null;
						}
						ScheduleDepthRetry(uid, current);
						return;
					}

					current.PendingBook = book;
					if (current.Live && current.AvailableBookDepth == depth)
					{
						PromotePendingBook(uid, current);
					}
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
					if (!_entries.TryGetValue(uid, out var current) || current != entry || current.BookVersion != version)
					{
						return;
					}

					current.BookBootstrapping = false;
					foreach (var subscriber in current.Subscribers.Where(s => !s.Active && s.DepthLevels > 0 && !CanServe(current, s.DepthLevels)).ToArray())
					{
						subscriber.Ready.TrySetException(new TInvestRequestException("T-Invest market-depth snapshot is unavailable; retry later."));
						Remove(uid, subscriber);
					}
					TryReady(uid, current);
					ScheduleDepthRetry(uid, current);
				});
			}
		}
	}

	void ScheduleDepthRetry(string uid, Entry entry)
	{
		if (!entry.Live || !NeedsDepthBootstrap(entry) || !entry.Subscribers.Any(s => s.DepthLevels > 0))
		{
			return;
		}

		var version = entry.BookVersion;
		var seconds = Math.Min(8, Math.Pow(2, Math.Min(entry.BookRetryCount++, 3)) * 0.5);
		_ = RetryAsync();
		async Task RetryAsync()
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(seconds), _clock, entry.Stop.Token).ConfigureAwait(false);
				Post(() =>
				{
					if (_entries.TryGetValue(uid, out var current) && current == entry && current.BookVersion == version && NeedsDepthBootstrap(current))
					{
						StartDepthSnapshot(uid, current);
					}
				});
			}
			catch (OperationCanceledException) when (entry.Stop.IsCancellationRequested || _stop.IsCancellationRequested)
			{
			}
		}
	}

	bool CanServe(Entry entry, int levels) => entry.Live && entry.AvailableBookDepth >= levels && entry.Book is { } book && book.Depth >= levels &&
		book.IsConsistent && DepthBook.IsStructurallyConsistent(book) && !entry.BookResyncPending;

	void BookDepthReady(string uid, int depth)
	{
		if (!_entries.TryGetValue(uid, out var entry) || entry.UpstreamDepth != depth)
		{
			return;
		}

		var depthChanged = entry.AvailableBookDepth != depth;
		entry.AvailableBookDepth = depth;
		if (entry.Book is { } currentBook && currentBook.Depth > depth)
		{
			entry.Book = DepthBook.Limit(currentBook with { Depth = depth }, depth);
		}
		if (entry.PendingBook is { } pendingBook && pendingBook.Depth > depth)
		{
			entry.PendingBook = null;
		}

		if (entry.PendingBook is { } pending && pending.Depth == depth)
		{
			if (entry.Book == null || !DepthBook.IsOlder(pending.Time, entry.Book.Time))
			{
				PromotePendingBook(uid, entry);
			}
			else
			{
				entry.PendingBook = null;
				ScheduleDepthRetry(uid, entry);
			}
		}
		else if (depthChanged && entry.Subscribers.Any(s => s.DepthLevels > 0) && NeedsDepthBootstrap(entry))
		{
			StartDepthSnapshot(uid, entry);
		}

		TryReady(uid, entry);
	}

	void PromotePendingBook(string uid, Entry entry)
	{
		if (entry.PendingBook is not { } book || entry.AvailableBookDepth != book.Depth || entry.UpstreamDepth != book.Depth ||
			(entry.Book != null && DepthBook.IsOlder(book.Time, entry.Book.Time)))
		{
			return;
		}

		var isSnapshot = entry.Book == null || entry.BookResyncPending;
		entry.PendingBook = null;
		entry.BookFence = null;
		entry.BookResyncPending = false;
		entry.BookRetryCount = 0;
		PublishBook(uid, entry, book, isSnapshot);
	}

	void PublishBook(string uid, Entry entry, TInvestOrderBook book, bool forceSnapshot)
	{
		var previous = entry.Book;
		var isSnapshot = forceSnapshot || previous == null;
		entry.Book = book;
		entry.HasBook = true;
		var quote = DepthBook.BestQuote(book);
		var quoteChanged = UpdateSnapshotQuote(entry, quote);
		if (!entry.Live)
		{
			return;
		}

		foreach (var subscriber in entry.Subscribers.Where(s => s.Active))
		{
			if (subscriber.DepthLevels > 0)
			{
				var view = DepthBook.Limit(book, subscriber.DepthLevels);
				if (isSnapshot)
				{
					subscriber.Receive(new HubDepthSnapshot(view, subscriber.DepthLevels));
				}
				else
				{
					var previousView = DepthBook.Limit(previous!, subscriber.DepthLevels);
					var changes = DepthBook.Diff(previousView, view, subscriber.DepthLevels);
					if (changes.Length != 0)
					{
						subscriber.Receive(new HubDepthUpdate(view, subscriber.DepthLevels));
					}
				}
			}
			else if (quoteChanged)
			{
				subscriber.Receive(new HubUpdate(quote));
			}
		}

		TryReady(uid, entry);
	}

	bool UpdateSnapshotQuote(Entry entry, TInvestQuote quote)
	{
		var old = entry.Snapshot.Quote;
		if (old != null && DepthBook.IsOlder(quote.Time, old.Time))
		{
			return false;
		}
		entry.Snapshot = entry.Snapshot with { Quote = quote };
		return old == null || old.Bid != quote.Bid || old.Ask != quote.Ask || old.BidLots != quote.BidLots || old.AskLots != quote.AskLots;
	}

	void TryReady(string uid, Entry entry)
	{
		if (!entry.Live)
		{
			return;
		}

		foreach (var subscriber in entry.Subscribers.Where(s => !s.Active && (s.RecordingOnly || (s.DepthLevels > 0 ? CanServe(entry, s.DepthLevels) : entry.HasSnapshot))).ToArray())
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
				if (subscriber.DepthLevels > 0 && entry.Book != null)
				{
					subscriber.Receive(new HubDepthSnapshot(DepthBook.Limit(entry.Book, subscriber.DepthLevels), subscriber.DepthLevels));
				}
				else
				{
					subscriber.Receive(new HubSnapshot(entry.Snapshot));
				}
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
			var desiredDepth = RequiredDepth(entry);
			if (desiredDepth != entry.UpstreamDepth)
			{
				if (desiredDepth < entry.UpstreamDepth && entry.Book is { } currentBook && currentBook.Depth > desiredDepth)
				{
					entry.Book = DepthBook.Limit(currentBook with { Depth = desiredDepth }, desiredDepth);
				}

				entry.UpstreamDepth = desiredDepth;
				entry.BookVersion++;
				entry.PendingBook = null;
				entry.BookBootstrapping = false;
				UpdateDesired(entry.Stream);
				if (entry.Live && NeedsDepthBootstrap(entry))
				{
					StartDepthSnapshot(uid, entry);
				}
			}
			return;
		}

		_recorder.SetLive(uid, false);
		entry.Stop.Cancel();
		_entries.Remove(uid);
		UpdateDesired(entry.Stream);
	}

	void UpdateDesired(int stream) => _streams[stream].SetDesired(_entries.Where(e => e.Value.Stream == stream).ToDictionary(e => e.Key, e => e.Value.UpstreamDepth, StringComparer.Ordinal));
	void Reject(string uid, TInvestSubscriptionStatus status) => Reject(uid, TInvestSubscriptionKind.Book, status);

	void Reject(string uid, TInvestSubscriptionKind kind, TInvestSubscriptionStatus status)
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
		if (kind != TInvestSubscriptionKind.Book)
		{
			foreach (var subscriber in entry.Subscribers.Where(s => !s.Active).ToArray())
			{
				subscriber.Ready.TrySetException(new TInvestRequestException(text));
				Remove(uid, subscriber);
			}
			Availability(uid, false);
			return;
		}

		var fallbackDepth = entry.Book is { } book && entry.AvailableBookDepth > 0 && !entry.BookResyncPending &&
			book.IsConsistent && DepthBook.IsStructurallyConsistent(book) ? Math.Min(book.Depth, entry.AvailableBookDepth) : 0;
		foreach (var subscriber in entry.Subscribers.Where(s => !s.Active && !s.RecordingOnly &&
			(fallbackDepth == 0 || (s.DepthLevels > 0 && s.DepthLevels > fallbackDepth))).ToArray())
		{
			subscriber.Ready.TrySetException(new TInvestRequestException(text));
			Remove(uid, subscriber);
		}

		var activeCovered = entry.Subscribers.Where(s => s.Active && !s.RecordingOnly)
			.All(s => s.DepthLevels == 0 ? fallbackDepth >= 1 : s.DepthLevels <= fallbackDepth);
		if (!activeCovered || (fallbackDepth == 0 && entry.Subscribers.Any(s => !s.RecordingOnly)))
		{
			Availability(uid, false);
		}
		else
		{
			TryReady(uid, entry);
		}
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
			entry.BookVersion++;
			entry.Book = null;
			entry.PendingBook = null;
			entry.HasBook = false;
			entry.AvailableBookDepth = 0;
			entry.BookFence = null;
			entry.BookBootstrapping = false;
			entry.BookResyncPending = true;
			entry.BookRetryCount = 0;
		}

		foreach (var subscriber in entry.Subscribers.Where(s => s.Active))
		{
			subscriber.Receive(new HubFeedStatus(available));
		}

		if (available)
		{
			if (entry.Subscribers.Any(s => !s.Active && !s.RecordingOnly && s.DepthLevels == 0) && !entry.HasSnapshot && !entry.Bootstrapping)
			{
				StartSnapshot(uid, entry);
			}
			if (entry.Subscribers.Any(s => s.DepthLevels > 0) && NeedsDepthBootstrap(entry))
			{
				StartDepthSnapshot(uid, entry);
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
			TInvestOrderBook b => b.Uid,
			TInvestStatus s => s.Uid,
			_ => null
		};
		if (uid == null || !_entries.TryGetValue(uid, out var entry))
		{
			return;
		}

		if (item is TInvestOrderBook sourceBook)
		{
			if ((entry.BookFence is { } fence && DepthBook.IsOlder(sourceBook.Time, fence)) ||
				(entry.Book is { } current && DepthBook.IsOlder(sourceBook.Time, current.Time)))
			{
				return;
			}

			var depth = sourceBook.Depth;
			if (depth <= 0)
			{
				MarkDepthBookForResync(uid, entry, sourceBook.Time);
				return;
			}
			if (depth != entry.UpstreamDepth && depth != entry.AvailableBookDepth && depth != entry.Book?.Depth)
			{
				return;
			}
			if (!sourceBook.IsConsistent || !DepthBook.IsStructurallyConsistent(sourceBook))
			{
				MarkDepthBookForResync(uid, entry, sourceBook.Time);
				return;
			}

			var coveredDepth = Math.Min(depth, entry.UpstreamDepth);
			var book = DepthBook.Limit(sourceBook with { Depth = coveredDepth }, coveredDepth);
			if (depth == entry.UpstreamDepth)
			{
				if ((entry.Book != null && DepthBook.IsOlder(book.Time, entry.Book.Time)) ||
					(entry.PendingBook != null && DepthBook.IsOlder(book.Time, entry.PendingBook.Time)))
				{
					if (entry.AvailableBookDepth == depth)
					{
						ScheduleDepthRetry(uid, entry);
					}
					return;
				}

				entry.BookVersion++;
				entry.BookBootstrapping = false;
				entry.PendingBook = book;
				if (entry.Live && entry.AvailableBookDepth == depth)
				{
					PromotePendingBook(uid, entry);
				}
				else
				{
					TryReady(uid, entry);
				}
				return;
			}

			if (entry.Book != null && DepthBook.IsOlder(book.Time, entry.Book.Time))
			{
				return;
			}

			entry.BookFence = null;
			entry.BookResyncPending = false;
			PublishBook(uid, entry, book, entry.Book == null);
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
		// Keep normalized quotes for alternate streams; the SDK reports TInvestOrderBook events.
		else if (item is TInvestQuote quote)
		{
			if (!quote.Consistent || (entry.Snapshot.Quote != null && DepthBook.IsOlder(quote.Time, entry.Snapshot.Quote.Time)))
			{
				return;
			}

			var old = entry.Snapshot.Quote;
			entry.Snapshot = entry.Snapshot with { Quote = quote };
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
			foreach (var subscriber in entry.Subscribers.Where(s => s.Active && s.DepthLevels == 0))
			{
				subscriber.Receive(new HubUpdate(item));
			}
		}
	}

	void MarkDepthBookForResync(string uid, Entry entry, TInvestTimestamp fence)
	{
		entry.Book = null;
		entry.HasBook = false;
		entry.PendingBook = null;
		entry.BookResyncPending = true;
		if (entry.BookFence is not { } currentFence || DepthBook.IsOlder(currentFence, fence))
		{
			entry.BookFence = fence;
		}

		if (!entry.BookBootstrapping)
		{
			StartDepthSnapshot(uid, entry);
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
		public Subscriber(Action<HubEvent> receive, bool snapshotOnly, DateTimeOffset deadline, CancellationToken cancellation, bool recordingOnly, int depthLevels)
		{
			Receive = receive;
			SnapshotOnly = snapshotOnly;
			Deadline = deadline;
			Cancellation = cancellation;
			RecordingOnly = recordingOnly;
			DepthLevels = depthLevels;
		}

		public DateTimeOffset Deadline { get; }
		public CancellationToken Cancellation { get; }
		public Action<HubEvent> Receive { get; }
		public bool SnapshotOnly { get; }
		public bool RecordingOnly { get; }
		public int DepthLevels { get; }
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
		public int UpstreamDepth { get; set; } = 1;
		public TInvestOrderBook? Book { get; set; }
		public TInvestOrderBook? PendingBook { get; set; }
		public bool HasBook { get; set; }
		public int AvailableBookDepth { get; set; }
		public bool BookBootstrapping { get; set; }
		public bool BookResyncPending { get; set; }
		public int BookVersion { get; set; }
		public TInvestTimestamp? BookFence { get; set; }
		public int BookRetryCount { get; set; }
	}
}
