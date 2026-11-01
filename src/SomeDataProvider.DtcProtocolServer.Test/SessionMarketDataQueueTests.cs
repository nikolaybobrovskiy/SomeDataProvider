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
