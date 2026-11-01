namespace SomeDataProvider.DataStorage.TInvest.Test;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public sealed class ScriptedMarketDataStream : ITInvestMarketDataStream
{
	readonly Channel<TInvestMarketEvent> _events = Channel.CreateUnbounded<TInvestMarketEvent>();
	readonly Dictionary<TInvestSubscriptionKind, HashSet<string>> _actual = new();
	public List<TInvestStreamRequest> Requests { get; } = new();
	public Func<TInvestSubscriptionKind, string, TInvestSubscriptionStatus>? Status { get; set; }
	public Action<TInvestStreamRequest, ScriptedMarketDataStream>? Sending { get; set; }
	public bool AutoReply { get; set; } = true;
	public bool OmitEmptyKinds { get; set; }
	public bool Disposed { get; private set; }
	public int Queries { get; private set; }

	public Task<IReadOnlyList<string>> SendAsync(TInvestStreamRequest request, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (Requests)
		{
			request = request with
			{
				Uids = request.Uids.Where(uid => request.IsWanted?.Invoke(uid) ?? true).ToArray()
			};
			if (request.Kind != TInvestSubscriptionKind.All && request.Uids.Count == 0)
			{
				return Task.FromResult(request.Uids);
			}

			Requests.Add(request);
			Sending?.Invoke(request, this);
			if (!AutoReply)
			{
				return Task.FromResult(request.Uids);
			}

			if (request.Kind == TInvestSubscriptionKind.All)
			{
				Queries++;
				foreach (var kind in new[]
				{
					TInvestSubscriptionKind.Trades,
					TInvestSubscriptionKind.Book,
					TInvestSubscriptionKind.Info
				}

				)
				{
					var current = _actual.GetValueOrDefault(kind, new());
					if (!OmitEmptyKinds || current.Count != 0)
					{
						Emit(new TInvestSubscriptionReply(kind, current.Select(uid => new TInvestSubscriptionElement(uid, TInvestSubscriptionStatus.Success)).ToArray()));
					}
				}

				return Task.FromResult(request.Uids);
			}

			if (!_actual.TryGetValue(request.Kind, out var actual))
			{
				_actual[request.Kind] = actual = new();
			}

			var elements = request.Uids.Select(uid => new TInvestSubscriptionElement(uid, Status?.Invoke(request.Kind, uid) ?? TInvestSubscriptionStatus.Success)).ToArray();
			foreach (var element in elements.Where(e => e.Status == TInvestSubscriptionStatus.Success))
			{
				if (request.Subscribe)
				{
					actual.Add(element.Uid);
				}
				else
				{
					actual.Remove(element.Uid);
				}
			}

			Emit(new TInvestSubscriptionReply(request.Kind, elements));
		}

		return Task.FromResult(request.Uids);
	}

	public void Forget(TInvestSubscriptionKind kind, string uid)
	{
		if (_actual.TryGetValue(kind, out var actual))
		{
			actual.Remove(uid);
		}
	}

	public void Emit(TInvestMarketEvent item) => _events.Writer.TryWrite(item);
	public void Break() => _events.Writer.TryComplete(new TInvestRequestException("Invented stream failure."));
	public async IAsyncEnumerable<TInvestMarketEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken))
		{
			yield return item;
		}
	}

	public void Dispose()
	{
		Disposed = true;
		_events.Writer.TryComplete();
	}
}
