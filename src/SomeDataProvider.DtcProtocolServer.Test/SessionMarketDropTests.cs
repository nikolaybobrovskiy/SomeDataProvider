namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Reflection;
using Dtc.Protocol;
using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Market;

public partial class SessionTests
{
	[Test]
	public void HeartbeatsReportOneThenZeroThenALaterNewDrop()
	{
		var clock = new ManualTimeProvider();
		using var connection = Connect(new FakeHistory(0), onlyHistoryServer: false, timeProvider: clock);
		Logon(connection, 10);
		var queue = new SessionMarketDataQueue(1);
		typeof(Session).GetField("_marketQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection.ServerSession, queue);
		Drop();
		clock.Advance(TimeSpan.FromSeconds(10));
		AssertDrops(1);
		AssertResponsive(connection);
		clock.Advance(TimeSpan.FromSeconds(10));
		AssertDrops(0);
		AssertResponsive(connection);
		Drop();
		clock.Advance(TimeSpan.FromSeconds(10));
		AssertDrops(1);
		AssertResponsive(connection);
		clock.Advance(TimeSpan.FromSeconds(10));
		AssertDrops(0);
		void Drop()
		{
			queue.Enqueue(1, Array.Empty<byte>());
			queue.Enqueue(1, Array.Empty<byte>());
			queue.Clear();
		}

		void AssertDrops(uint expected)
		{
			var bytes = connection.ReadFrame();
			DtcBinaryDecoder.Decode(bytes, out s_Heartbeat heartbeat);
			Assert.That(heartbeat.NumDroppedMessages, Is.EqualTo(expected));
		}
	}
}
