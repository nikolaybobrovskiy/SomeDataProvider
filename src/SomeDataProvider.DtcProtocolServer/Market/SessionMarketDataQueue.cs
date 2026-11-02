namespace SomeDataProvider.DtcProtocolServer.Market;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Market updates only. Control frames bypass this queue. The session sends at most one queued update at a time.</summary>
sealed class SessionMarketDataQueue
{
	readonly int _limit;
	readonly Queue<Update> _updates = new();
	readonly Dictionary<uint, byte[]> _repairQuotes = new();
	readonly Dictionary<uint, DepthRepair> _repairDepths = new();
	uint _dropped;
	public SessionMarketDataQueue(int limit)
	{
		if (limit < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(limit));
		}

		_limit = limit;
	}

	public int Count => _updates.Count;

	// The session lock serializes these methods with sends and heartbeats.
	public bool Enqueue(uint symbolId, byte[] bytes, bool quote = false)
	{
		RepairDepths();
		RepairQuotes();
		if (_updates.Count == _limit)
		{
			AddDropped(1);
			if (quote)
			{
				_repairQuotes[symbolId] = bytes;
			}

			return false;
		}

		_updates.Enqueue(new(symbolId, bytes, quote, false));
		return true;
	}

	/// <summary>Enqueues a complete snapshot atomically, deferring it if the bounded queue has no room.</summary>
	public void EnqueueDepthSnapshot(uint symbolId, IReadOnlyList<byte[]> snapshot)
	{
		if (snapshot.Count == 0)
		{
			throw new ArgumentException("A depth snapshot must contain its batch markers.", nameof(snapshot));
		}

		RemoveQueuedDepth(symbolId, countDropped: false);
		var batch = snapshot.ToArray();
		if (_repairDepths.ContainsKey(symbolId))
		{
			_repairDepths[symbolId] = new(batch.Length, () => batch);
			RepairDepths();
			return;
		}
		RepairDepths();
		if (_updates.Count + batch.Length > _limit)
		{
			_repairDepths[symbolId] = new(batch.Length, () => batch);
			return;
		}

		EnqueueDepthBatch(symbolId, batch);
	}

	/// <summary>Enqueues a delta batch or replaces the symbol's pending deltas with a fresh full snapshot.</summary>
	public bool EnqueueDepthUpdate(uint symbolId, IReadOnlyList<byte[]> updates, int latestSnapshotFrameCount, Func<IReadOnlyList<byte[]>> latestSnapshot)
	{
		if (latestSnapshotFrameCount < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(latestSnapshotFrameCount));
		}
		ArgumentNullException.ThrowIfNull(latestSnapshot);
		if (updates.Count == 0)
		{
			return true;
		}
		if (_repairDepths.ContainsKey(symbolId))
		{
			AddDropped((uint)updates.Count);
			_repairDepths[symbolId] = new(latestSnapshotFrameCount, latestSnapshot);
			RepairDepths();
			return false;
		}
		RepairDepths();
		if (_updates.Count + updates.Count <= _limit)
		{
			EnqueueDepthBatch(symbolId, updates);
			return true;
		}

		AddDropped((uint)updates.Count);
		RemoveQueuedDepth(symbolId, countDropped: true);
		_repairDepths[symbolId] = new(latestSnapshotFrameCount, latestSnapshot);
		RepairDepths();
		return false;
	}

	public byte[]? Dequeue()
	{
		RepairDepths();
		RepairQuotes();
		if (!_updates.TryDequeue(out var update))
		{
			return null;
		}

		RepairDepths();
		RepairQuotes();
		return update.Bytes;
	}

	public void Remove(uint symbolId)
	{
		RemoveQueued(symbolId, _ => true);
		_repairQuotes.Remove(symbolId);
		_repairDepths.Remove(symbolId);
	}

	public void RemoveMarketData(uint symbolId)
	{
		RemoveQueued(symbolId, update => !update.IsDepth);
		_repairQuotes.Remove(symbolId);
	}

	public void RemoveDepth(uint symbolId)
	{
		RemoveQueued(symbolId, update => update.IsDepth);
		_repairDepths.Remove(symbolId);
	}

	public uint TakeDropped()
	{
		var dropped = _dropped;
		_dropped = 0;
		return dropped;
	}

	public void Clear()
	{
		_updates.Clear();
		_repairQuotes.Clear();
		_repairDepths.Clear();
	}

	internal bool IsDepthRepairPending(uint symbolId) => _repairDepths.ContainsKey(symbolId);

	void EnqueueDepthBatch(uint symbolId, IEnumerable<byte[]> batch)
	{
		foreach (var bytes in batch)
		{
			_updates.Enqueue(new(symbolId, bytes, false, true));
		}
	}

	void RemoveQueuedDepth(uint symbolId, bool countDropped)
	{
		var removed = RemoveQueued(symbolId, update => update.IsDepth);
		if (countDropped)
		{
			AddDropped((uint)removed);
		}
	}

	int RemoveQueued(uint symbolId, Func<Update, bool> predicate)
	{
		var keep = new List<Update>(_updates.Count);
		var removed = 0;
		while (_updates.TryDequeue(out var update))
		{
			if (update.SymbolId == symbolId && predicate(update))
			{
				removed++;
			}
			else
			{
				keep.Add(update);
			}
		}
		foreach (var update in keep)
		{
			_updates.Enqueue(update);
		}
		return removed;
	}

	void RepairDepths()
	{
		foreach (var repair in _repairDepths.ToArray())
		{
			if (_updates.Count + repair.Value.FrameCount > _limit)
			{
				continue;
			}

			var snapshot = repair.Value.CreateSnapshot();
			if (snapshot.Count != repair.Value.FrameCount)
			{
				throw new ArgumentException("A depth repair snapshot must match its declared frame count.", nameof(repair));
			}
			_repairDepths.Remove(repair.Key);
			EnqueueDepthBatch(repair.Key, snapshot);
		}
	}

	void RepairQuotes()
	{
		while (_updates.Count < _limit && _repairQuotes.Count != 0)
		{
			var quote = _repairQuotes.First();
			_repairQuotes.Remove(quote.Key);
			_updates.Enqueue(new(quote.Key, quote.Value, true, false));
		}
	}

	void AddDropped(uint count)
	{
		_dropped = uint.MaxValue - _dropped < count ? uint.MaxValue : _dropped + count;
	}

	readonly record struct DepthRepair(int FrameCount, Func<IReadOnlyList<byte[]>> CreateSnapshot);

	readonly record struct Update(uint SymbolId, byte[] Bytes, bool IsQuote, bool IsDepth);
}
