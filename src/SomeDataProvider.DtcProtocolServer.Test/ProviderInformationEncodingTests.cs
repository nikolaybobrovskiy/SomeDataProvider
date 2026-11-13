namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Buffers;
using System.Linq;
using System.Reflection;
using Dtc.Protocol.Private;
using NUnit.Framework;

public partial class ProviderInformationWireTests
{
	[Test]
	public void PooledProviderFramesMatchZeroedEncodingAndOwnTheirUsedBytes()
	{
		var encode = typeof(Session).GetMethod("EncodeProviderMessage", BindingFlags.Static | BindingFlags.NonPublic)!;
		var messages = new ProviderInformationMessage[]
		{
			new SymbolCatalogNode { Catalog = "fred", NodeID = "fred/17", Name = new string('x', 1024), SnapshotVersion = "synthetic-view", RowsInPage = 1 },
			new SymbolCatalogEntry { Catalog = "t-invest", NodeID = "synthetic-board", Symbol = "SYNTH-TESTCC", Name = "Invented market instrument", Kind = 1, Features = 111, UIDEpoch = "synthetic-epoch", SnapshotVersion = "synthetic-view", RowsInPage = 1 },
			new SymbolCatalogEntry { Catalog = "fred", NodeID = "fred/17", Symbol = "fred-SYNTHSERIES", Name = "Invented series", SnapshotVersion = "synthetic-view", RowsInPage = 1 },
			new SymbolCatalogEnd { Catalog = "fred", NodeID = "fred/17", RowCount = 1, Consistency = 2, SnapshotVersion = "synthetic-view" },
			new SymbolCatalogEnd { Catalog = "fred", Total = 0, RowCount = 0, Consistency = 2, SnapshotVersion = "synthetic-view" },
			new ProviderHelloResponse { BudgetScopeID = "synthetic-budget", Features = ProviderInformationFeatures.SymbolCatalogV1 },
			new ProviderRequestReject { FailedOperation = SymbolCatalogRequest.MessageType, ReasonCode = "InvalidCursor", Text = "Synthetic rejection" },
		};
		byte[]? retained = null;
		byte[]? expectedRetained = null;
		foreach (var message in messages)
		{
			var zeroed = new byte[ushort.MaxValue];
			var count = ProviderInformationCodec.EncodeVls(message, zeroed);
			var dirty = Enumerable.Repeat((byte)0xff, ushort.MaxValue).ToArray();
			var dirtyCount = ProviderInformationCodec.EncodeVls(message, dirty);
			Assert.That(dirtyCount, Is.EqualTo(count));
			Assert.That(dirty.AsSpan(0, count).SequenceEqual(zeroed.AsSpan(0, count)), Is.True, "The used frame must not depend on old buffer contents.");

			var rented = ArrayPool<byte>.Shared.Rent(ushort.MaxValue);
			rented.AsSpan().Fill(0xff);
			ArrayPool<byte>.Shared.Return(rented);
			var actual = (byte[])encode.Invoke(null, new object[] { message })!;
			Assert.That(actual.Length, Is.EqualTo(count));
			Assert.That(actual.AsSpan().SequenceEqual(zeroed.AsSpan(0, count)), Is.True);
			if (retained != null) Assert.That(retained, Is.EqualTo(expectedRetained), "A later shorter frame must not overwrite an already returned frame.");
			retained = actual;
			expectedRetained = zeroed.AsSpan(0, count).ToArray();
		}
	}
}
