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
		RepairQuotes();
		if (_updates.Count == _limit)
		{
			if (_dropped != uint.MaxValue)
			{
				_dropped++;
			}

			if (quote)
			{
				_repairQuotes[symbolId] = bytes;
			}

			return false;
		}

		_updates.Enqueue(new(symbolId, bytes));
		return true;
	}

	public byte[]? Dequeue()
	{
		if (!_updates.TryDequeue(out var update))
		{
			return null;
		}

		RepairQuotes();
		return update.Bytes;
	}

	public void Remove(uint symbolId)
	{
		var keep = _updates.Where(u => u.SymbolId != symbolId).ToArray();
		_updates.Clear();
		foreach (var update in keep)
		{
			_updates.Enqueue(update);
		}

		_repairQuotes.Remove(symbolId);
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
	}

	void RepairQuotes()
	{
		while (_updates.Count < _limit && _repairQuotes.Count != 0)
		{
			var quote = _repairQuotes.First();
			_repairQuotes.Remove(quote.Key);
			_updates.Enqueue(new(quote.Key, quote.Value));
		}
	}

	readonly record struct Update(uint SymbolId, byte[] Bytes);
}
