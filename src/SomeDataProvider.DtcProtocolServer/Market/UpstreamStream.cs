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
	readonly Func<double> _jitter;
	readonly Action<DateTimeOffset>? _progress;
	readonly Action<string>? _diagnostic;
	readonly CancellationTokenSource _stop = new();
	readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
	{
		FullMode = BoundedChannelFullMode.DropWrite
	});
	HashSet<string> _desired = new(StringComparer.Ordinal);
	CancellationTokenSource? _cycle;
	bool _idleClose;
	bool _disposed;
	public UpstreamStream(ITInvestGateway gateway, TimeProvider clock, TimeSpan silence, Action<TInvestMarketEvent> publish, Action<string, bool> available, Action<string, TInvestSubscriptionStatus> rejected, Func<double>? jitter = null, Action<DateTimeOffset>? progress = null, Action<string>? diagnostic = null)
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
		Completion = Task.Run(RunAsync);
	}

	public Task Completion { get; }

	public void SetDesired(IEnumerable<string> uids)
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_desired = new(uids, StringComparer.Ordinal);
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
						var blocked = new HashSet<string>(StringComparer.Ordinal);
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
								await SyncAsync(stream, replies.Reader, sent, cycle.Token, blocked).ConfigureAwait(false);
								if (verify)
								{
									blocked = await VerifyAsync(stream, replies.Reader, sent, cycle.Token).ConfigureAwait(false);
									verify = false;
								}

								PublishReady(sent, blocked);
							}
							catch (TInvestRequestException ex) when (ex.Kind == TInvestFailureKind.Limit)
							{
								// Admission was refused before any write. Existing subscriptions remain healthy.
								_diagnostic?.Invoke("Subscription quota delayed.");
								await Task.Delay(TimeSpan.FromSeconds(1), _clock, cycle.Token).ConfigureAwait(false);
								continue;
							}

							var retry = blocked.Count != 0 || Desired().Any(uid => Kinds.Any(k => !sent[k].Contains(uid)));
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

	void PublishReady(Dictionary<TInvestSubscriptionKind, HashSet<string>> sent, HashSet<string> blocked)
	{
		foreach (var uid in Desired())
		{
			if (!blocked.Contains(uid) && Kinds.All(k => sent[k].Contains(uid)))
			{
				_available(uid, true);
			}
		}
	}

	async Task SyncAsync(ITInvestMarketDataStream stream, ChannelReader<TInvestSubscriptionReply> replies, Dictionary<TInvestSubscriptionKind, HashSet<string>> sent, CancellationToken ct, HashSet<string>? blocked = null)
	{
		foreach (var kind in Kinds)
		{
			var desired = Desired().ToHashSet(StringComparer.Ordinal);
			await ChangeAsync(stream, replies, sent[kind], kind, sent[kind].Except(desired).ToArray(), false, ct).ConfigureAwait(false);
			await ChangeAsync(stream, replies, sent[kind], kind, desired.Except(sent[kind]).Where(uid => blocked?.Contains(uid) != true).ToArray(), true, ct).ConfigureAwait(false);
		}
	}

	async Task ChangeAsync(ITInvestMarketDataStream stream, ChannelReader<TInvestSubscriptionReply> replies, HashSet<string> sent, TInvestSubscriptionKind kind, string[] uids, bool subscribe, CancellationToken ct)
	{
		if (uids.Length == 0)
		{
			return;
		}

		var deadline = _clock.GetUtcNow().AddSeconds(20);
		uids = (await stream.SendAsync(new(kind, uids, subscribe, uid => subscribe == Desired().Contains(uid, StringComparer.Ordinal)), deadline, ct).ConfigureAwait(false)).ToArray();
		if (uids.Length == 0)
		{
			return;
		}

		var reply = await ReplyAsync(replies, kind, uids, deadline, ct).ConfigureAwait(false);
		foreach (var uid in uids)
		{
			var status = reply.Elements.FirstOrDefault(e => e.Uid == uid)?.Status ?? TInvestSubscriptionStatus.Unspecified;
			if (status == TInvestSubscriptionStatus.Success)
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
			else
			{
				_rejected(uid, status);
			}
		}
	}

	async Task<HashSet<string>> VerifyAsync(ITInvestMarketDataStream stream, ChannelReader<TInvestSubscriptionReply> replies, Dictionary<TInvestSubscriptionKind, HashSet<string>> sent, CancellationToken ct)
	{
		var blocked = new HashSet<string>(StringComparer.Ordinal);
		// GetMySubscriptions returns the three subscription response kinds. Compare the actual sets, retry missing
		// elements once, then leave mismatches unavailable until the next reconnect.
		for (var attempt = 0; attempt < 2; attempt++)
		{
			var deadline = _clock.GetUtcNow().AddSeconds(20);
			await stream.SendAsync(new(TInvestSubscriptionKind.All, Array.Empty<string>()), deadline, ct).ConfigureAwait(false);
			var actual = Kinds.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal));
			var received = new HashSet<TInvestSubscriptionKind>();
			using var collect = new CancellationTokenSource(TimeSpan.FromSeconds(2), _clock);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, collect.Token);
			while (received.Count < Kinds.Length)
			{
				try
				{
					var reply = await replies.ReadAsync(linked.Token).ConfigureAwait(false);
					received.Add(reply.Kind);
					actual[reply.Kind] = reply.Elements.Where(e => e.Status == TInvestSubscriptionStatus.Success).Select(e => e.Uid).ToHashSet(StringComparer.Ordinal);
				}
				catch (OperationCanceledException) when (collect.IsCancellationRequested && !ct.IsCancellationRequested)
				{
					break;
				}
			}

			var missing = Kinds.ToDictionary(k => k, k => sent[k].Except(actual[k]).Intersect(Desired()).ToArray());
			if (missing.Values.All(v => v.Length == 0))
			{
				return blocked;
			}

			foreach (var kind in Kinds)
			{
				foreach (var uid in missing[kind])
				{
					sent[kind].Remove(uid);
				}

				if (attempt == 0)
				{
					await ChangeAsync(stream, replies, sent[kind], kind, missing[kind], true, ct).ConfigureAwait(false);
				}
				else
				{
					foreach (var uid in missing[kind])
					{
						blocked.Add(uid);
						_available(uid, false);
					}
				}
			}
		}

		return blocked;
	}

	async Task<TInvestSubscriptionReply> ReplyAsync(ChannelReader<TInvestSubscriptionReply> replies, TInvestSubscriptionKind kind, string[] written, DateTimeOffset deadline, CancellationToken ct)
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
			if (reply.Kind == kind && reply.Elements.Any(e => written.Contains(e.Uid, StringComparer.Ordinal)))
			{
				return reply;
			}
		}
	}
}
