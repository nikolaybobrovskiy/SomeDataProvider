// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Buffers.Binary;
	using System.Collections.Generic;
	using System.Linq;

	using Dtc.Protocol.Private;

	using NUnit.Framework;

	using SomeDataProvider.DtcProtocolServer.Data;

	// The private series extension v1 (docs/dtc-series-extension.md in MrktPltfrm) through a real session. Requests are
	// encoded and answers decoded with Dtc.Protocol.Private, the definitions the platform's client uses. No test calls FRED.
	public partial class SessionTests
	{
		const string KnownSeriesSymbol = "fred-" + FakeStores.SeriesId;

		// §6: the server answers the handshake on the streaming and the history-only server alike, and keeps no state.
		[Test]
		public void SeriesExtensionRequest_IsAnsweredWithVersion1AndBothFeatures([Values] bool onlyHistoryServer)
		{
			using var connection = Connect(new FakeHistory(0), onlyHistoryServer: onlyHistoryServer);

			connection.Send(SeriesWire.Encode(new SeriesExtensionRequest { RequestID = 41, Version = 1 }));

			var response = SeriesWire.DecodeExtensionResponse(connection.ReadFrame());
			connection.AssertNothingMoreArrives();
			Assert.That(response, Is.EqualTo(new SeriesExtensionResponse
			{
				RequestID = 41,
				Version = 1,
				Features = SeriesExtensionFeatures.Metadata | SeriesExtensionFeatures.Vintages,
			}));
		}

		// §5: the version both sides use is the smaller of the two highest versions; 0 means none in common.
		[TestCase(2, 1, SeriesExtensionFeatures.Metadata | SeriesExtensionFeatures.Vintages)]
		[TestCase(0, 0, SeriesExtensionFeatures.None)]
		[TestCase(-1, 0, SeriesExtensionFeatures.None)]
		public void SeriesExtensionRequest_AgreesOnTheSmallerVersion(int clientVersion, int agreedVersion, SeriesExtensionFeatures features)
		{
			using var connection = Connect(new FakeHistory(0));

			connection.Send(SeriesWire.Encode(new SeriesExtensionRequest { RequestID = 42, Version = clientVersion }));

			var response = SeriesWire.DecodeExtensionResponse(connection.ReadFrame());
			Assert.That(response, Is.EqualTo(new SeriesExtensionResponse { RequestID = 42, Version = agreedVersion, Features = features }));
		}

		[Test]
		public void SeriesMetadataRequest_IsAnsweredWithTheMetadataAndEchoesTheRequest()
		{
			var stores = new FakeStores(new FakeHistory(0));
			using var connection = Connect(stores);

			// No handshake first: the server keeps no handshake state.
			connection.Send(SeriesWire.Encode(new SeriesMetadataRequest { RequestID = 43, Symbol = KnownSeriesSymbol }));

			var response = SeriesWire.DecodeMetadataResponse(connection.ReadFrame());
			connection.AssertNothingMoreArrives();
			Assert.That(response, Is.EqualTo(FakeStores.SeriesMetadata with { RequestID = 43, Symbol = KnownSeriesSymbol, Exchange = string.Empty }));
			Assert.That(stores.SeriesRequestCount, Is.EqualTo(1));
		}

		[TestCase("fred-UNKNOWN", SeriesRequestRejectReason.UnknownSymbol, null)]
		[TestCase(FakeStores.FailingSeriesSymbol, SeriesRequestRejectReason.SourceUnavailable, 30)]
		public void SeriesMetadataRequest_TheSourceRefuses_IsRejectedOnce(string symbol, SeriesRequestRejectReason reason, int? retryAfterSeconds)
		{
			using var connection = Connect(new FakeHistory(0));

			connection.Send(SeriesWire.Encode(new SeriesMetadataRequest { RequestID = 44, Symbol = symbol }));

			var reject = SeriesWire.DecodeReject(connection.ReadFrame());
			connection.AssertNothingMoreArrives();
			Assert.That(reject.RequestID, Is.EqualTo(44));
			Assert.That(reject.RejectReason, Is.EqualTo(reason));
			Assert.That(reject.RetryAfter, Is.EqualTo(retryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null));
			Assert.That(reject.RejectText, Is.Not.Empty);
		}

		// Profile v1: symbols have no exchange; a request without a symbol, or with one far longer than any FRED symbol, is
		// invalid. The source is not asked.
		[TestCase(KnownSeriesSymbol, "NYSE")]
		[TestCase("", "")]
		[TestCase(null, "")]
		public void SeriesMetadataRequest_WithAnInvalidSymbolOrExchange_IsRejectedWithoutAskingTheSource(string? symbol, string exchange)
		{
			var stores = new FakeStores(new FakeHistory(0));
			using var connection = Connect(stores);
			symbol ??= "fred-" + new string('A', Session.MaxSeriesSymbolLength);

			connection.Send(SeriesWire.Encode(new SeriesMetadataRequest { RequestID = 45, Symbol = symbol, Exchange = exchange }));

			var reject = SeriesWire.DecodeReject(connection.ReadFrame());
			connection.AssertNothingMoreArrives();
			Assert.That(reject.RequestID, Is.EqualTo(45));
			Assert.That(reject.RejectReason, Is.EqualTo(SeriesRequestRejectReason.InvalidRequest));
			Assert.That(stores.SeriesRequestCount, Is.Zero);
		}

		// §5: rows in ascending observation date and then real-time start, a missing value as a row without a value,
		// ReleaseTime unset for FRED, and exactly one terminator.
		[Test]
		public void SeriesVintagesRequest_IsAnsweredWithTheRowsInOrderAndOneTerminator()
		{
			using var connection = Connect(new FakeHistory(0));

			connection.Send(SeriesWire.Encode(new SeriesVintagesRequest { RequestID = 46, Symbol = KnownSeriesSymbol }));

			var answer = ReadVintagesAnswer(connection, 46);
			connection.AssertNothingMoreArrives();
			Assert.That(answer.Reject, Is.Null);
			Assert.That(answer.Rows.Select(ToVintage), Is.EqualTo(FakeStores.DefaultVintageBatches.SelectMany(b => b)));
			Assert.That(answer.Rows.Select(r => r.ReleaseTime), Is.All.Null);
			Assert.That(answer.Rows[2].Value, Is.Null, "The missing value is not a row without a value.");
		}

		[Test]
		public void SeriesVintagesRequest_WithNoRows_IsAnsweredWithTheTerminatorAlone()
		{
			var stores = new FakeStores(new FakeHistory(0)) { VintageBatches = Array.Empty<IReadOnlyList<SeriesVintage>>() };
			using var connection = Connect(stores);

			connection.Send(SeriesWire.Encode(new SeriesVintagesRequest { RequestID = 47, Symbol = KnownSeriesSymbol }));

			var answer = ReadVintagesAnswer(connection, 47);
			connection.AssertNothingMoreArrives();
			Assert.That(answer.Rows, Is.Empty);
			Assert.That(answer.Reject, Is.Null);
		}

		// More rows than one send holds arrive complete and in order.
		[Test]
		public void SeriesVintagesRequest_WithManyRows_SendsThemAllInOrder()
		{
			var start = new DateOnly(1990, 1, 1);
			var rows = Enumerable.Range(0, (2 * SeriesVintagesStreamer.RowsPerSend) + 7)
				.Select(i => new SeriesVintage(start.AddDays(i), start.AddDays(i + 1), new DateOnly(9999, 12, 31), i * 0.25))
				.ToArray();
			var stores = new FakeStores(new FakeHistory(0)) { VintageBatches = new[] { rows[..100], rows[100..] } };
			using var connection = Connect(stores);

			connection.Send(SeriesWire.Encode(new SeriesVintagesRequest { RequestID = 48, Symbol = KnownSeriesSymbol }));

			var answer = ReadVintagesAnswer(connection, 48);
			connection.AssertNothingMoreArrives();
			Assert.That(answer.Reject, Is.Null);
			Assert.That(answer.Rows.Select(ToVintage), Is.EqualTo(rows));
		}

		// §5: 0 bounds mean the earliest or latest available; the source receives them as null and set bounds unchanged.
		[TestCase(0, 0, 0, 0)]
		[TestCase(1970_01_01, 2025_12_31, 1776_07_04, 2026_06_30)]
		[TestCase(0, 0, 2026_03_15, 2026_03_15)]
		public void SeriesVintagesRequest_PassesItsWindowsToTheSource(int observationStart, int observationEnd, int realtimeStart, int realtimeEnd)
		{
			var stores = new FakeStores(new FakeHistory(0));
			using var connection = Connect(stores);
			var request = new SeriesVintagesRequest
			{
				RequestID = 49,
				Symbol = KnownSeriesSymbol,
				ObservationStart = ToDate(observationStart),
				ObservationEnd = ToDate(observationEnd),
				RealtimeStart = ToDate(realtimeStart),
				RealtimeEnd = ToDate(realtimeEnd),
			};

			connection.Send(SeriesWire.Encode(request));

			ReadVintagesAnswer(connection, 49);
			Assert.That(stores.LastVintagesRequest, Is.EqualTo(request));
		}

		[TestCase(2026_01_02, 2026_01_01, 0, 0)]
		[TestCase(0, 0, 2026_01_02, 2026_01_01)]
		public void SeriesVintagesRequest_WithAStartAfterItsEnd_IsRejectedWithoutAskingTheSource(int observationStart, int observationEnd, int realtimeStart, int realtimeEnd)
		{
			var stores = new FakeStores(new FakeHistory(0));
			using var connection = Connect(stores);

			connection.Send(SeriesWire.Encode(new SeriesVintagesRequest
			{
				RequestID = 50,
				Symbol = KnownSeriesSymbol,
				ObservationStart = ToDate(observationStart),
				ObservationEnd = ToDate(observationEnd),
				RealtimeStart = ToDate(realtimeStart),
				RealtimeEnd = ToDate(realtimeEnd),
			}));

			var answer = ReadVintagesAnswer(connection, 50);
			connection.AssertNothingMoreArrives();
			Assert.That(answer.Rows, Is.Empty);
			Assert.That(answer.Reject?.RejectReason, Is.EqualTo(SeriesRequestRejectReason.InvalidRequest));
			Assert.That(stores.SeriesRequestCount, Is.Zero);
		}

		// The record decodes a date that is not YYYYMMDD as null; the server must not widen the window to all history.
		[TestCase(20)] // ObservationStart
		[TestCase(24)] // ObservationEnd
		[TestCase(28)] // RealtimeStart
		[TestCase(32)] // RealtimeEnd
		public void SeriesVintagesRequest_WithANonzeroDateThatIsNoDate_IsRejected(int dateOffset)
		{
			var stores = new FakeStores(new FakeHistory(0));
			using var connection = Connect(stores);
			var frame = SeriesWire.Encode(new SeriesVintagesRequest { RequestID = 51, Symbol = KnownSeriesSymbol });
			BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(dateOffset), 2026_02_30);

			connection.Send(frame);

			var reject = SeriesWire.DecodeReject(connection.ReadFrame());
			connection.AssertNothingMoreArrives();
			Assert.That(reject.RequestID, Is.EqualTo(51));
			Assert.That(reject.RejectReason, Is.EqualTo(SeriesRequestRejectReason.InvalidRequest));
			Assert.That(reject.RejectText, Does.Contain("20260230"));
			Assert.That(stores.SeriesRequestCount, Is.Zero);
		}

		[Test]
		public void SeriesVintagesRequest_ForAnUnknownSymbol_IsRejected()
		{
			using var connection = Connect(new FakeHistory(0));

			connection.Send(SeriesWire.Encode(new SeriesVintagesRequest { RequestID = 52, Symbol = "fred-UNKNOWN" }));

			var answer = ReadVintagesAnswer(connection, 52);
			connection.AssertNothingMoreArrives();
			Assert.That(answer.Rows, Is.Empty);
			Assert.That(answer.Reject?.RejectReason, Is.EqualTo(SeriesRequestRejectReason.UnknownSymbol));
		}

		// A failure after rows went out ends the answer with one reject, never with a terminator that would claim it complete.
		[Test]
		public void SeriesVintagesRequest_FailingAfterABatch_SendsItsRowsAndThenOneReject()
		{
			var stores = new FakeStores(new FakeHistory(0)) { VintagesFailAfterBatches = 1 };
			using var connection = Connect(stores);

			connection.Send(SeriesWire.Encode(new SeriesVintagesRequest { RequestID = 53, Symbol = KnownSeriesSymbol }));

			var answer = ReadVintagesAnswer(connection, 53);
			connection.AssertNothingMoreArrives();
			Assert.That(answer.Rows.Select(ToVintage), Is.EqualTo(FakeStores.DefaultVintageBatches[0]));
			Assert.That(answer.Reject?.RejectReason, Is.EqualTo(SeriesRequestRejectReason.SourceUnavailable));
			Assert.That(answer.Reject?.RetryAfter, Is.EqualTo(FakeStores.FailureRetryAfter));
		}

		// §7: a malformed request whose RequestID can be read gets a reject, and the connection stays usable.
		[Test]
		public void SeriesMetadataRequest_Malformed_IsRejectedAndTheConnectionStaysOpen()
		{
			using var connection = Connect(new FakeHistory(0));
			var frame = SeriesWire.Encode(new SeriesMetadataRequest { RequestID = 54, Symbol = KnownSeriesSymbol });
			// The Symbol's Length now reaches past the end of the message.
			BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(14), (ushort)(frame.Length + 10));

			connection.Send(frame);

			var reject = SeriesWire.DecodeReject(connection.ReadFrame());
			Assert.That(reject.RequestID, Is.EqualTo(54));
			Assert.That(reject.RejectReason, Is.EqualTo(SeriesRequestRejectReason.InvalidRequest));
			connection.Send(SeriesWire.Encode(new SeriesExtensionRequest { RequestID = 55, Version = 1 }));
			Assert.That(SeriesWire.DecodeExtensionResponse(connection.ReadFrame()).RequestID, Is.EqualTo(55));
		}

		// DTC: a Type the server does not implement is dropped without a reply, and the session goes on.
		[Test]
		public void PrivateTypeOutsideTheRequests_IsDroppedAndTheConnectionStaysOpen()
		{
			using var connection = Connect(new FakeHistory(0));
			var frame = new byte[12];
			BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)frame.Length);
			BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), DtcPrivateMessages.SeriesExtensionResponseType);
			BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)frame.Length);

			connection.Send(frame);
			connection.AssertNothingMoreArrives();

			connection.Send(SeriesWire.Encode(new SeriesExtensionRequest { RequestID = 56, Version = 1 }));
			Assert.That(SeriesWire.DecodeExtensionResponse(connection.ReadFrame()).RequestID, Is.EqualTo(56));
		}

		static SeriesVintage ToVintage(SeriesVintageRecord row) =>
			new(row.ObservationDate!.Value, row.RealtimeStart!.Value, row.RealtimeEnd!.Value, row.Value);

		static DateOnly? ToDate(int yyyymmdd) =>
			yyyymmdd == 0 ? null : new DateOnly(yyyymmdd / 10_000, yyyymmdd / 100 % 100, yyyymmdd % 100);

		static VintagesAnswer ReadVintagesAnswer(Connection connection, int requestId)
		{
			var frames = new List<byte[]>();
			while (true)
			{
				var frame = connection.ReadFrame();
				frames.Add(frame);
				if (SeriesWire.EndsVintagesAnswer(frame))
					return SeriesWire.DecodeVintagesAnswer(frames, requestId);
			}
		}
	}
}
