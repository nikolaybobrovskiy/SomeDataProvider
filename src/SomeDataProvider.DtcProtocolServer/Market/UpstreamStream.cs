namespace SomeDataProvider.DtcProtocolServer.Market;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SomeDataProvider.DataStorage.TInvest;

/// <summary>Owns one duplex call. Every generation has one reader and one serialized, coalescing writer.</summary>
sealed class UpstreamStream : IDisposable
{
	static readonly TInvestSubscriptionKind[] Kinds =
	{
		TInvestSubscriptionKind.Trades,
		TInvestSubscriptionKind.Book,
		TInvestSubscriptionKind.Info
	};
	readonly object _gate = new();
	readonly ITInvestGateway _gateway;
	readonly TimeProvider _clock;
	readonly TimeSpan _silence;
	readonly Action<TInvestMarketEvent> _publish;
	readonly Action<string, bool> _available;
	readonly Action<string, TInvestSubscriptionStatus> _rejected;
	readonly Action<string, TInvestSubscriptionKind, TInvestSubscriptionStatus>? _rejectedWithKind;
	readonly Action<string, int>? _bookReady;
	readonly Func<double> _jitter;
	readonly Action<DateTimeOffset>? _progress;
	readonly Action<string>? _diagnostic;
	readonly CancellationTokenSource _stop = new();
	readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
	{
		FullMode = BoundedChannelFullMode.DropWrite
	});
	HashSet<string> _desired = new(StringComparer.Ordinal);
	Dictionary<string, int> _desiredBookDepths = new(StringComparer.Ordinal);
	CancellationTokenSource? _cycle;
	bool _idleClose;
	bool _disposed;
	long _desiredVersion;
	public UpstreamStream(ITInvestGateway gateway, TimeProvider clock, TimeSpan silence, Action<TInvestMarketEvent> publish, Action<string, bool> available, Action<string, TInvestSubscriptionStatus> rejected, Func<double>? jitter = null, Action<DateTimeOffset>? progress = null, Action<string>? diagnostic = null, Action<string, TInvestSubscriptionKind, TInvestSubscriptionStatus>? rejectedWithKind = null, Action<string, int>? bookReady = null)
	{
		_gateway = gateway;
		_clock = clock;
		_silence = silence;
		_publish = publish;
		_available = available;
		_rejected = rejected;
		_jitter = jitter ?? Random.Shared.NextDouble;
		_progress = progress;
		_diagnostic = diagnostic;
		_rejectedWithKind = rejectedWithKind;
		_bookReady = bookReady;
		Completion = Task.Run(RunAsync);
	}

	public Task Completion { get; }

	public void SetDesired(IEnumerable<string> uids)
		=> SetDesired(uids.Distinct(StringComparer.Ordinal).ToDictionary(uid => uid, _ => 1, StringComparer.Ordinal));

	public void SetDesired(IReadOnlyDictionary<string, int> bookDepths)
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			if (bookDepths.Any(e => string.IsNullOrEmpty(e.Key) || e.Value is not (1 or 10 or 20 or 30 or 40 or 50)))
			{
				throw new ArgumentException("Desired book depths must map UIDs to supported depths 1, 10, 20, 30, 40 or 50.", nameof(bookDepths));
			}

			_desiredBookDepths = bookDepths.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
			_desired = new(bookDepths.Keys, StringComparer.Ordinal);
			_desiredVersion++;
			if (_desired.Count == 0)
			{
				_idleClose = true;
				_cycle?.Cancel();
			}
		}

		_changes.Writer.TryWrite(true);
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_stop.Cancel();
			_cycle?.Cancel();
		}
	}

	string[] Desired()
	{
		lock (_gate)
			return _desired.ToArray();
	}

	Dictionary<string, int> DesiredBookDepths()
	{
		lock (_gate)
			return new(_desiredBookDepths, StringComparer.Ordinal);
	}

	async Task RunAsync()
	{
		var failures = 0;
		try
		{
			while (!_stop.IsCancellationRequested)
			{
				if (Desired().Length == 0)
				{
					await _changes.Reader.ReadAsync(_stop.Token).ConfigureAwait(false);
					continue;
				}

				using var cycle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
				lock (_gate)
				{
					_cycle = cycle;
					_idleClose = _desired.Count == 0;
					if (_desired.Count == 0)
					{
						cycle.Cancel();
					}
				}

				var opened = _clock.GetTimestamp();
				var intentionalClose = false;
				try
				{
					using var stream = await _gateway.OpenMarketDataStreamAsync(cycle.Token).ConfigureAwait(false);
					var replies = Channel.CreateUnbounded<TInvestSubscriptionReply>();
					var lastMessage = _clock.GetTimestamp();
					using var watchdog = _clock.CreateTimer(_ =>
					{
						if (_clock.GetElapsedTime(Interlocked.Read(ref lastMessage)) >= _silence)
						{
							cycle.Cancel();
						}
					}, null, _silence, TimeSpan.FromSeconds(1));
					var reader = ReadAsync();
					try
					{
						var sent = Kinds.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal));
						var sentBooks = new HashSet<(string Uid, int Depth)>();
						var blocked = new HashSet<string>(StringComparer.Ordinal);
						var rejectedBookDepths = new Dictionary<string, int>(StringComparer.Ordinal);
						var verify = true;
						while (true)
						{
							await Task.Delay(TimeSpan.FromMilliseconds(20), _clock, cycle.Token).ConfigureAwait(false);
							while (_changes.Reader.TryRead(out _))
							{
							}

							blocked.RemoveWhere(uid => !Desired().Contains(uid, StringComparer.Ordinal));
							try
							{
								await SyncAsync(stream, replies.Reader, sent, sentBooks, cycle.Token, blocked, rejectedBookDepths).ConfigureAwait(false);
								if (verify)
								{
									blocked = await VerifyAsync(stream, replies.Reader, sent, sentBooks, rejectedBookDepths, cycle.Token).ConfigureAwait(false);
									verify = false;
								}

								PublishReady(sent, sentBooks, blocked);
							}
							catch (TInvestRequestException ex) when (ex.Kind == TInvestFailureKind.Limit)
							{
								// Admission was refused before any write. Existing subscriptions remain healthy.
								_diagnostic?.Invoke("Subscription quota delayed.");
								await Task.Delay(TimeSpan.FromSeconds(1), _clock, cycle.Token).ConfigureAwait(false);
								continue;
							}

							var desiredBooks = DesiredBookDepths();
							var desiredBookPairs = desiredBooks.Select(e => (Uid: e.Key, Depth: e.Value)).ToHashSet();
							var retry = blocked.Count != 0 || Desired().Any(uid => Kinds.Any(k => !sent[k].Contains(uid))) ||
								desiredBookPairs.Any(pair => !sentBooks.Contains(pair)) ||
								sentBooks.Any(pair => !desiredBookPairs.Contains(pair));
							using var wait = CancellationTokenSource.CreateLinkedTokenSource(cycle.Token);
							if (retry)
							{
								_diagnostic?.Invoke("Subscription retry scheduled.");
								using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30), _clock);
								using var linked = CancellationTokenSource.CreateLinkedTokenSource(wait.Token, budget.Token);
								try
								{
									await _changes.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
								}
								catch (OperationCanceledException) when (budget.IsCancellationRequested && !cycle.IsCancellationRequested)
								{
									blocked.Clear();
									rejectedBookDepths.Clear();
									verify = true;
								}
							}
							else
							{
								await _changes.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
							}
						}
					}
					finally
					{
						cycle.Cancel();
						stream.Dispose();
						try
						{
							await reader.ConfigureAwait(false);
						}
						catch (Exception)
						{ /* The generation already failed. */
						}
					}

					async Task ReadAsync()
					{
						try
						{
							await foreach (var item in stream.ReadAllAsync(cycle.Token).ConfigureAwait(false))
							{
								Interlocked.Exchange(ref lastMessage, _clock.GetTimestamp());
								_publish(item);
								_progress?.Invoke(_clock.GetUtcNow());
								if (item is TInvestSubscriptionReply reply)
								{
									replies.Writer.TryWrite(reply);
								}
							}
						}
						finally
						{
							replies.Writer.TryComplete();
							cycle.Cancel();
						}
					}
				}
				catch (Exception) when (!_stop.IsCancellationRequested)
				{
					lock (_gate)
						intentionalClose = _idleClose;
					if (!intentionalClose)
					{
						_diagnostic?.Invoke("Stream disconnected; reconnect scheduled.");
						// No SDK exception text or payload enters the hub or its clients.
						foreach (var uid in Desired())
						{
							_available(uid, false);
						}
					}
				}
				finally
				{
					lock (_gate)
					{
						intentionalClose |= _idleClose;
						_cycle = null;
					}
				}

				if (_stop.IsCancellationRequested)
				{
					break;
				}

				// Empty-to-nonempty changes can arrive before the cancelled generation finishes.
				// Its deliberate close must not penalize the replacement subscription or later real failures.
				if (intentionalClose || Desired().Length == 0)
				{
					failures = 0;
					continue;
				}

				if (_clock.GetElapsedTime(opened) >= TimeSpan.FromSeconds(30))
				{
					failures = 0;
				}

				var backoff = Math.Min(30, Math.Pow(2, Math.Min(failures++, 5)));
				await Task.Delay(TimeSpan.FromSeconds(backoff * (0.8 + 0.4 * _jitter())), _clock, _stop.Token).ConfigureAwait(false);
			}
		}
		catch (Exception) when (_stop.IsCancellationRequested)
		{
		}
	}

	void PublishReady(Dictionary<TInvestSubscriptionKind, HashSet<string>> sent, HashSet<(string Uid, int Depth)> sentBooks, HashSet<string> blocked)
	{
		var desiredBooks = DesiredBookDepths();
		foreach (var uid in Desired())
		{
			if (!blocked.Contains(uid) && Kinds.All(k => sent[k].Contains(uid)) &&
				desiredBooks.TryGetValue(uid, out var desiredDepth) && sentBooks.Contains((uid, desiredDepth)))
			{
				_available(uid, true);
				_bookReady?.Invoke(uid, desiredDepth);
			}
		}
	}

	async Task SyncAsync(ITInvestMarketDataStream stream, ChannelReader<TInvestSubscriptionReply> replies, Dictionary<TInvestSubscriptionKind, HashSet<string>> sent, HashSet<(string Uid, int Depth)> sentBooks, CancellationToken ct, HashSet<string>? blocked, Dictionary<string, int> rejectedBookDepths)
	{
		foreach (var kind in Kinds.Where(kind => kind != TInvestSubscriptionKind.Book))
		{
			var desired = Desired().ToHashSet(StringComparer.Ordinal);
			await ChangeAsync(stream, replies, sent[kind], kind, sent[kind].Except(desired).ToArray(), false, ct).ConfigureAwait(false);
			await ChangeAsync(stream, replies, sent[kind], kind, desired.Except(sent[kind]).Where(uid => blocked?.Contains(uid) != true).ToArray(), true, ct).ConfigureAwait(false);
		}

		await SyncBooksAsync(stream, replies, sent[TInvestSubscriptionKind.Book], sentBooks, blocked, rejectedBookDepths, ct).ConfigureAwait(false);
	}

	async Task SyncBooksAsync(ITInvestMarketDataStream stream, ChannelReader<TInvestSubscriptionReply> replies, HashSet<string> sent, HashSet<(string Uid, int Depth)> sentBooks, HashSet<string>? blocked, Dictionary<string, int> rejectedBookDepths, CancellationToken ct)
	{
		var desired = DesiredBookDepths();
		foreach (var uid in rejectedBookDepths.Where(item => !desired.TryGetValue(item.Key, out var targetDepth) || targetDepth != item.Value).Select(item => item.Key).ToArray())
		{
			rejectedBookDepths.Remove(uid);
		}
		var obsolete = sentBooks.Where(pair =>
			(!desired.TryGetValue(pair.Uid, out var targetDepth) || targetDepth != pair.Depth) &&
			!(rejectedBookDepths.TryGetValue(pair.Uid, out var failedDepth) && desired.TryGetValue(pair.Uid, out targetDepth) &&
				targetDepth == failedDepth && pair.Depth < targetDepth)).ToArray();
		var fallbackDepths = obsolete.Where(pair => desired.TryGetValue(pair.Uid, out var targetDepth) && targetDepth > pair.Depth)
			.GroupBy(pair => pair.Uid, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Max(pair => pair.Depth), StringComparer.Ordinal);
		foreach (var group in obsolete.GroupBy(pair => pair.Depth))
		{
			await ChangeBookAsync(stream, replies, sent, sentBooks, group.Select(pair => pair.Uid).ToArray(), false, group.Key, fallbackDepths, rejectedBookDepths, false, ct).ConfigureAwait(false);
		}

		desired = DesiredBookDepths();
		var desiredPairs = desired.Select(e => (Uid: e.Key, Depth: e.Value)).ToHashSet();
		var replacements = desiredPairs.Where(pair => !sentBooks.Contains(pair) &&
			!sentBooks.Any(current => current.Uid == pair.Uid) && blocked?.Contains(pair.Uid) != true &&
			(!rejectedBookDepths.TryGetValue(pair.Uid, out var failedDepth) || failedDepth != pair.Depth));
		foreach (var group in replacements.GroupBy(pair => pair.Depth))
		{
			await ChangeBookAsync(stream, replies, sent, sentBooks, group.Select(pair => pair.Uid).ToArray(), true, group.Key, fallbackDepths, rejectedBookDepths, false, ct).ConfigureAwait(false);
		}

		desired = DesiredBookDepths();
		var restore = fallbackDepths.Where(item => rejectedBookDepths.TryGetValue(item.Key, out var failedDepth) &&
			desired.TryGetValue(item.Key, out var targetDepth) && targetDepth == failedDepth &&
			!sentBooks.Any(pair => pair.Uid == item.Key) && blocked?.Contains(item.Key) != true);
		foreach (var group in restore.GroupBy(item => item.Value))
		{
			await ChangeBookAsync(stream, replies, sent, sentBooks, group.Select(item => item.Key).ToArray(), true, group.Key, fallbackDepths, rejectedBookDepths, true, ct).ConfigureAwait(false);
		}
	}

	async Task ChangeBookAsync(ITInvestMarketDataStream stream, ChannelReader<TInvestSubscriptionReply> replies, HashSet<string> sent, HashSet<(string Uid, int Depth)> sentBooks, string[] uids, bool subscribe, int depth, Dictionary<string, int> fallbackDepths, Dictionary<string, int> rejectedBookDepths, bool restoringFallback, CancellationToken ct)
	{
		if (uids.Length == 0)
		{
			return;
		}

		bool IsWanted(string uid)
		{
			var desired = DesiredBookDepths();
			if (!subscribe)
			{
				return sentBooks.Contains((uid, depth)) && (!desired.TryGetValue(uid, out var currentDepth) || currentDepth != depth);
			}

			var desiredMatch = desired.TryGetValue(uid, out var wantedDepth) && wantedDepth == depth;
			var fallbackMatch = restoringFallback && fallbackDepths.TryGetValue(uid, out var fallbackDepth) && fallbackDepth == depth &&
				desired.TryGetValue(uid, out wantedDepth) && wantedDepth > depth && rejectedBookDepths.TryGetValue(uid, out var failedDepth) && failedDepth == wantedDepth;
			return (desiredMatch || fallbackMatch) && !sentBooks.Contains((uid, depth));
		}

		var deadline = _clock.GetUtcNow().AddSeconds(20);
		uids = (await stream.SendAsync(new(TInvestSubscriptionKind.Book, uids, subscribe, IsWanted, depth), deadline, ct).ConfigureAwait(false)).ToArray();
		if (uids.Length == 0)
		{
			return;
		}

		var reply = await ReplyAsync(replies, TInvestSubscriptionKind.Book, uids, deadline, ct, depth).ConfigureAwait(false);
		foreach (var uid in uids)
		{
			var element = reply.Elements.FirstOrDefault(e => e.Uid == uid && (e.Depth == 0 || e.Depth == depth));
			var status = element?.Status ?? TInvestSubscriptionStatus.Unspecified;
			if (status == TInvestSubscriptionStatus.Success || !subscribe && status == TInvestSubscriptionStatus.SubscriptionNotFound)
			{
				if (subscribe)
				{
					sentBooks.Add((uid, depth));
					sent.Add(uid);
					if (!restoringFallback)
					{
						rejectedBookDepths.Remove(uid);
					}
				}
				else if (sentBooks.Remove((uid, depth)) && !sentBooks.Any(pair => pair.Uid == uid))
				{
					sent.Remove(uid);
				}
			}
			else if (subscribe && IsWanted(uid))
			{
				ReportRejected(uid, TInvestSubscriptionKind.Book, status);
				if (!restoringFallback && fallbackDepths.TryGetValue(uid, out var fallbackDepth) && fallbackDepth < depth)
				{
					rejectedBookDepths[uid] = depth;
				}
				else if (restoringFallback || !sentBooks.Any(pair => pair.Uid == uid))
				{
					_available(uid, false);
				}
			}
			else if (!subscribe && IsWanted(uid))
			{
				_diagnostic?.Invoke("Old book subscription removal failed; replacement remains delayed.");
			}
		}
	}

	async Task ChangeAsync(ITInvestMarketDataStream stream, ChannelReader<TInvestSubscriptionReply> replies, HashSet<string> sent, TInvestSubscriptionKind kind, string[] uids, bool subscribe, CancellationToken ct)
	{
		if (uids.Length == 0)
		{
			return;
		}

		var deadline = _clock.GetUtcNow().AddSeconds(20);
		bool IsWanted(string uid) => subscribe == Desired().Contains(uid, StringComparer.Ordinal);
		uids = (await stream.SendAsync(new(kind, uids, subscribe, IsWanted), deadline, ct).ConfigureAwait(false)).ToArray();
		if (uids.Length == 0)
		{
			return;
		}

		var reply = await ReplyAsync(replies, kind, uids, deadline, ct).ConfigureAwait(false);
		foreach (var uid in uids)
		{
			var status = reply.Elements.FirstOrDefault(e => e.Uid == uid)?.Status ?? TInvestSubscriptionStatus.Unspecified;
			if (status == TInvestSubscriptionStatus.Success || !subscribe && status == TInvestSubscriptionStatus.SubscriptionNotFound)
			{
				if (subscribe)
				{
					sent.Add(uid);
				}
				else
				{
					sent.Remove(uid);
				}
			}
			else if (IsWanted(uid))
			{
				ReportRejected(uid, kind, status);
			}
		}
	}

	async Task<HashSet<string>> VerifyAsync(ITInvestMarketDataStream stream, ChannelReader<TInvestSubscriptionReply> replies, Dictionary<TInvestSubscriptionKind, HashSet<string>> sent, HashSet<(string Uid, int Depth)> sentBooks, Dictionary<string, int> rejectedBookDepths, CancellationToken ct)
	{
		var blocked = new HashSet<string>(StringComparer.Ordinal);
		for (var attempt = 0; attempt < 2; attempt++)
		{
			long desiredVersion;
			lock (_gate)
			{
				desiredVersion = _desiredVersion;
			}
			var deadline = _clock.GetUtcNow().AddSeconds(20);
			await stream.SendAsync(new(TInvestSubscriptionKind.All, Array.Empty<string>()), deadline, ct).ConfigureAwait(false);
			var actual = Kinds.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal));
			var actualBooks = new HashSet<(string Uid, int Depth)>();
			var received = new HashSet<TInvestSubscriptionKind>();
			using var collect = new CancellationTokenSource(TimeSpan.FromSeconds(2), _clock);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, collect.Token);
			while (received.Count < Kinds.Length)
			{
				try
				{
					var reply = await replies.ReadAsync(linked.Token).ConfigureAwait(false);
					received.Add(reply.Kind);
					foreach (var element in reply.Elements.Where(e => e.Status == TInvestSubscriptionStatus.Success))
					{
						if (reply.Kind == TInvestSubscriptionKind.Book)
						{
							actualBooks.Add((element.Uid, element.Depth));
						actual[reply.Kind].Add(element.Uid);
						}
						else
						{
							actual[reply.Kind].Add(element.Uid);
						}
					}
				}
				catch (OperationCanceledException) when (collect.IsCancellationRequested && !ct.IsCancellationRequested)
				{
					break;
				}
			}

			ReplaceSent(actual, actualBooks, sent, sentBooks);
			string[] desiredUids;
			Dictionary<string, int> desiredBooks;
			lock (_gate)
			{
				if (desiredVersion != _desiredVersion)
				{
					return blocked;
				}
				desiredUids = _desired.ToArray();
				desiredBooks = new(_desiredBookDepths, StringComparer.Ordinal);
			}
			var desired = desiredUids.ToHashSet(StringComparer.Ordinal);
			var desiredBookPairs = desiredBooks.Select(e => (Uid: e.Key, Depth: e.Value)).ToHashSet();
			var mismatch = Kinds.Where(k => k != TInvestSubscriptionKind.Book).Any(k => !actual[k].SetEquals(desired)) ||
				!actualBooks.SetEquals(desiredBookPairs);
			if (!mismatch)
			{
				return blocked;
			}

			if (attempt == 0)
			{
				await SyncAsync(stream, replies, sent, sentBooks, ct, blocked, rejectedBookDepths).ConfigureAwait(false);
				continue;
			}

			lock (_gate)
			{
				if (desiredVersion != _desiredVersion)
				{
					return blocked;
				}

				foreach (var uid in desired)
				{
					var missingBook = !desiredBooks.TryGetValue(uid, out var wantedDepth) ||
						!actualBooks.Contains((uid, wantedDepth)) && !actualBooks.Any(pair => pair.Uid == uid && pair.Depth < wantedDepth);
					if (Kinds.Where(k => k != TInvestSubscriptionKind.Book).Any(k => !actual[k].Contains(uid)) || missingBook)
					{
						blocked.Add(uid);
						_available(uid, false);
					}
				}
			}
		}

		return blocked;
	}

	void ReplaceSent(Dictionary<TInvestSubscriptionKind, HashSet<string>> actual, HashSet<(string Uid, int Depth)> actualBooks, Dictionary<TInvestSubscriptionKind, HashSet<string>> sent, HashSet<(string Uid, int Depth)> sentBooks)
	{
		foreach (var kind in Kinds)
		{
			sent[kind].Clear();
			sent[kind].UnionWith(actual[kind]);
		}
		sentBooks.Clear();
		sentBooks.UnionWith(actualBooks);
	}

	void ReportRejected(string uid, TInvestSubscriptionKind kind, TInvestSubscriptionStatus status)
	{
		if (_rejectedWithKind != null)
		{
			_rejectedWithKind(uid, kind, status);
		}
		else
		{
			_rejected(uid, status);
		}
	}

	async Task<TInvestSubscriptionReply> ReplyAsync(ChannelReader<TInvestSubscriptionReply> replies, TInvestSubscriptionKind kind, string[] written, DateTimeOffset deadline, CancellationToken ct, int? expectedDepth = null)
	{
		while (true)
		{
			var remaining = deadline - _clock.GetUtcNow();
			if (remaining <= TimeSpan.Zero)
			{
				throw new TimeoutException();
			}

			using var budget = new CancellationTokenSource(remaining, _clock);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
			var reply = await replies.ReadAsync(linked.Token).ConfigureAwait(false);
			if (reply.Kind == kind && reply.Elements.Any(e => written.Contains(e.Uid, StringComparer.Ordinal) && (expectedDepth == null || e.Depth == 0 || e.Depth == expectedDepth)))
			{
				return reply;
			}
		}
	}
}
