namespace SomeDataProvider.DtcProtocolServer.Test;

using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Market;

[TestFixture]
public class SessionMarketDataQueueTests
{
	[Test]
	public void DropsArePerHeartbeatIntervalAndQuotesRepairWithoutAnotherChange()
	{
		var queue = new SessionMarketDataQueue(1);
		Assert.That(queue.Enqueue(1, new byte[]
		{
			1
		}), Is.True);
		Assert.That(queue.Enqueue(2, new byte[]
		{
			2
		}, quote: true), Is.False);
		Assert.That(queue.TakeDropped(), Is.EqualTo(1));
		Assert.That(queue.TakeDropped(), Is.Zero);
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[]
		{
			1
		}));
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[]
		{
			2
		}), "The dropped quote is repaired even if its source never changes again.");
		Assert.That(queue.Count, Is.Zero);
		Assert.That(queue.Enqueue(1, new byte[]
		{
			3
		}), Is.True);
		Assert.That(queue.Enqueue(1, new byte[]
		{
			4
		}), Is.False);
		Assert.That(queue.TakeDropped(), Is.EqualTo(1));
		Assert.That(queue.TakeDropped(), Is.Zero);
	}

	[Test]
	public void ReplacedQueuedDepthSnapshotDoesNotCountAsDropped()
	{
		var queue = new SessionMarketDataQueue(8);
		queue.EnqueueDepthSnapshot(4, new[] { new byte[] { 1 }, new byte[] { 2 } });
		var repairSnapshotCalls = 0;
		Assert.That(queue.EnqueueDepthUpdate(4, new[] { new byte[] { 3 } }, 2, () =>
		{
			repairSnapshotCalls++;
			return new[] { new byte[] { 4 }, new byte[] { 5 } };
		}), Is.True);
		Assert.That(repairSnapshotCalls, Is.Zero, "A repair snapshot is not encoded for a delta that fits the queue.");
		queue.EnqueueDepthSnapshot(4, new[] { new byte[] { 6 }, new byte[] { 7 } });

		Assert.That(queue.TakeDropped(), Is.Zero);
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[] { 6 }));
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[] { 7 }));
		Assert.That(queue.Dequeue(), Is.Null);
	}

	[Test]
	public void RepairFactoryIsLazyAndRunsOnceForEachOverflowReplacement()
	{
		var queue = new SessionMarketDataQueue(4);
		queue.Enqueue(9, new byte[] { 91 });
		queue.Enqueue(9, new byte[] { 92 });
		queue.EnqueueDepthSnapshot(4, new[] { new byte[] { 1 }, new byte[] { 2 } });
		var calls = 0;
		byte[][] Snapshot() => new[] { new byte[] { (byte)(10 * calls + 1) }, new byte[] { (byte)(10 * calls + 2) } };

		Assert.That(queue.EnqueueDepthUpdate(4, new[] { new byte[] { 3 } }, 2, () =>
		{
			calls++;
			return Snapshot();
		}), Is.False);
		Assert.That(calls, Is.EqualTo(1));
		Assert.That(queue.TakeDropped(), Is.EqualTo(3));
		Assert.That(queue.EnqueueDepthUpdate(4, new[] { new byte[] { 4 } }, 2, () =>
		{
			calls++;
			return Snapshot();
		}), Is.False);
		Assert.That(calls, Is.EqualTo(2));
		Assert.That(queue.TakeDropped(), Is.EqualTo(3));
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[] { 91 }));
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[] { 92 }));
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[] { 21 }));
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[] { 22 }));
		Assert.That(queue.Dequeue(), Is.Null);
	}

	[Test]
	public void PendingDepthRepairReplacesLostDeltasWithLatestAtomicSnapshot()
	{
		var queue = new SessionMarketDataQueue(4);
		queue.Enqueue(9, new byte[] { 91 });
		queue.Enqueue(9, new byte[] { 92 });
		queue.Enqueue(9, new byte[] { 93 });
		queue.EnqueueDepthSnapshot(4, new[] { new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 } });
		Assert.That(queue.IsDepthRepairPending(4), Is.True);

		var repairSnapshotCalls = 0;
		Assert.That(queue.EnqueueDepthUpdate(4, new[] { new byte[] { 4 }, new byte[] { 5 } }, 3, () =>
		{
			repairSnapshotCalls++;
			return new[] { new byte[] { (byte)(10 + repairSnapshotCalls) }, new byte[] { (byte)(20 + repairSnapshotCalls) }, new byte[] { (byte)(30 + repairSnapshotCalls) } };
		}), Is.False);
		Assert.That(repairSnapshotCalls, Is.Zero, "A full queue should retain a lazy factory instead of encoding a snapshot on every delta.");
		Assert.That(queue.EnqueueDepthUpdate(4, new[] { new byte[] { 9 } }, 3, () =>
		{
			repairSnapshotCalls++;
			return new[] { new byte[] { (byte)(10 + repairSnapshotCalls) }, new byte[] { (byte)(20 + repairSnapshotCalls) }, new byte[] { (byte)(30 + repairSnapshotCalls) } };
		}), Is.False);
		Assert.That(repairSnapshotCalls, Is.Zero, "Replacing a pending repair only replaces its snapshot factory.");
		Assert.That(queue.TakeDropped(), Is.EqualTo(3));
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[] { 91 }));
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[] { 92 }));
		Assert.That(repairSnapshotCalls, Is.EqualTo(1), "The latest snapshot is encoded once when the queue can fit the whole batch.");
		Assert.That(queue.IsDepthRepairPending(4), Is.False);
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[] { 93 }));
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[] { 11 }));
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[] { 21 }));
		Assert.That(queue.Dequeue(), Is.EqualTo(new byte[] { 31 }));
		Assert.That(queue.Dequeue(), Is.Null);
	}

	[Test]
	public void RemoveDepthPurgesQueuedSnapshotsAndPendingRepairs()
	{
		var queued = new SessionMarketDataQueue(4);
		queued.Enqueue(9, new byte[] { 90 });
		queued.EnqueueDepthSnapshot(4, new[] { new byte[] { 1 }, new byte[] { 2 } });
		Assert.That(queued.EnqueueDepthUpdate(4, new[] { new byte[] { 3 }, new byte[] { 4 } }, 2, () => new[] { new byte[] { 5 }, new byte[] { 6 } }), Is.False);
		Assert.That(queued.IsDepthRepairPending(4), Is.False, "The replacement snapshot fits behind unrelated queued data.");
		Assert.That(queued.Count, Is.EqualTo(3));

		queued.RemoveDepth(4);
		Assert.That(queued.Count, Is.EqualTo(1));
		Assert.That(queued.Dequeue(), Is.EqualTo(new byte[] { 90 }));
		Assert.That(queued.Dequeue(), Is.Null);
		Assert.That(queued.TakeDropped(), Is.EqualTo(4));

		var pending = new SessionMarketDataQueue(3);
		pending.Enqueue(9, new byte[] { 91 });
		pending.Enqueue(9, new byte[] { 92 });
		pending.Enqueue(9, new byte[] { 93 });
		pending.EnqueueDepthSnapshot(4, new[] { new byte[] { 7 }, new byte[] { 8 } });
		Assert.That(pending.IsDepthRepairPending(4), Is.True);
		pending.RemoveDepth(4);
		Assert.That(pending.IsDepthRepairPending(4), Is.False);
		Assert.That(pending.Count, Is.EqualTo(3));
		Assert.That(pending.Dequeue(), Is.EqualTo(new byte[] { 91 }));
		Assert.That(pending.Dequeue(), Is.EqualTo(new byte[] { 92 }));
		Assert.That(pending.Dequeue(), Is.EqualTo(new byte[] { 93 }));
		Assert.That(pending.Dequeue(), Is.Null);
	}

	[Test]
	public void UnsubscribePurgesQueuedValuesAndPendingQuoteRepairs()
	{
		var queue = new SessionMarketDataQueue(1);
		queue.Enqueue(1, new byte[]
		{
			1
		});
		queue.Enqueue(1, new byte[]
		{
			2
		}, true);
		queue.Remove(1);
		Assert.That(queue.Dequeue(), Is.Null);
		Assert.That(queue.Count, Is.Zero);
		Assert.That(queue.TakeDropped(), Is.EqualTo(1));
	}
}
