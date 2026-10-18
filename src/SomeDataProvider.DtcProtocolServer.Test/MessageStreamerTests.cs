// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Buffers.Binary;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;

	using NUnit.Framework;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary;

	/// <summary>
	/// Feeds the streamer the reads a TCP session can see: whole frames, several frames, and frames split at any byte.
	/// The frames are real client requests: Size includes the four-byte Size/Type header, as DTCProtocol.h defines it.
	/// </summary>
	[TestFixture]
	public class MessageStreamerTests
	{
		// s_Heartbeat: Size, Type, NumDroppedMessages @4, CurrentDateTime @8; 16 bytes.
		const ushort HeartbeatType = 3;
		const int HeartbeatSize = 16;

		static readonly byte[] SecurityDefinitionRequest = DtcWire.EncodeSecurityDefinitionForSymbolRequest(17, "fred-DGS10");
		static readonly byte[] HistoryRequest = DtcWire.EncodeHistoricalPriceDataRequest(23, "fred-DGS10", 1_600_000_000, 1_700_000_000);
		static readonly byte[] Heartbeat = EncodeHeartbeat(1_700_000_000);

		static IEnumerable<byte[]> Frames => new[] { SecurityDefinitionRequest, HistoryRequest, Heartbeat };

		[TestCaseSource(nameof(Frames))]
		public void OneFrame_InOneRead_IsPassedOn(byte[] frame)
		{
			var (streamer, received) = CreateStreamer();

			streamer.PutReceivedBytes(frame.ToArray());

			Assert.That(received, Is.EqualTo(new[] { frame }));
		}

		[Test]
		public void SeveralFrames_InOneRead_ArePassedOnInOrder()
		{
			var (streamer, received) = CreateStreamer();
			var frames = new[] { SecurityDefinitionRequest, HistoryRequest, Heartbeat, SecurityDefinitionRequest };

			streamer.PutReceivedBytes(Concat(frames));

			Assert.That(received, Is.EqualTo(frames));
		}

		// Split points 1–3 fall inside the Size/Type header; split point 1 falls between the two Size bytes.
		[TestCaseSource(nameof(Frames))]
		public void Frame_SplitIntoTwoReadsAtEveryByte_IsReassembled(byte[] frame)
		{
			for (var splitAt = 1; splitAt < frame.Length; splitAt++)
			{
				var (streamer, received) = CreateStreamer();

				streamer.PutReceivedBytes(frame.AsSpan(0, splitAt).ToArray());
				Assert.That(received, Is.Empty, $"Split at byte {splitAt}: the frame was passed on before all its bytes arrived.");
				streamer.PutReceivedBytes(frame.AsSpan(splitAt).ToArray());

				Assert.That(received, Is.EqualTo(new[] { frame }), $"Split at byte {splitAt}.");
			}
		}

		[Test]
		public void Frames_SplitIntoTwoReadsAtEveryByte_ArePassedOnInOrder()
		{
			var frames = new[] { HistoryRequest, Heartbeat, SecurityDefinitionRequest };
			var bytes = Concat(frames);
			for (var splitAt = 1; splitAt < bytes.Length; splitAt++)
			{
				var (streamer, received) = CreateStreamer();

				streamer.PutReceivedBytes(bytes.AsSpan(0, splitAt).ToArray());
				streamer.PutReceivedBytes(bytes.AsSpan(splitAt).ToArray());

				Assert.That(received, Is.EqualTo(frames), $"Split at byte {splitAt}.");
			}
		}

		[Test]
		public void Frames_ReceivedInReadsOfAnyLength_ArePassedOnInOrder([Values(1, 2, 3, 5, 7, 16, 33, 100)] int readLength)
		{
			var frames = new[] { SecurityDefinitionRequest, HistoryRequest, Heartbeat, HistoryRequest };
			var (streamer, received) = CreateStreamer();

			foreach (var read in Concat(frames).Chunk(readLength))
				streamer.PutReceivedBytes(read);

			Assert.That(received, Is.EqualTo(frames));
		}

		// A message larger than the reads that carry it: a request whose symbol is near the 4096-byte request-string cap.
		[Test]
		public void LargeFrame_ReceivedInSeveralReads_IsReassembled()
		{
			var frame = DtcWire.EncodeHistoricalPriceDataRequest(29, "fred-" + new string('A', 4000), 0, 0);
			var (streamer, received) = CreateStreamer();

			foreach (var read in Concat(new[] { Heartbeat, frame, Heartbeat }).Chunk(1500))
				streamer.PutReceivedBytes(read);

			Assert.That(received, Is.EqualTo(new[] { Heartbeat, frame, Heartbeat }));
		}

		// Size 0 used to repeat the same input forever; Sizes 1–3 framed messages shorter than their own header.
		[Test]
		public void SizeBelowHeader_IsRejected([Values(0, 1, 2, 3)] int size)
		{
			var (streamer, received) = CreateStreamer();

			Assert.Throws<InvalidDataException>(() => streamer.PutReceivedBytes(Concat(new[] { Heartbeat, HeaderOnly(size) })));

			Assert.That(received, Is.EqualTo(new[] { Heartbeat }), "Frames before the invalid one are passed on.");
		}

		[Test]
		public void SizeBelowHeader_SplitAfterItsFirstByte_IsRejectedWhenItsSecondByteArrives([Values(0, 1, 2, 3)] int size)
		{
			var (streamer, received) = CreateStreamer();
			var bytes = Concat(new[] { Heartbeat, HeaderOnly(size) });

			streamer.PutReceivedBytes(bytes.AsSpan(0, Heartbeat.Length + 1).ToArray());
			Assert.Throws<InvalidDataException>(() => streamer.PutReceivedBytes(bytes.AsSpan(Heartbeat.Length + 1).ToArray()));

			Assert.That(received, Is.EqualTo(new[] { Heartbeat }));
		}

		// After an invalid Size the frame boundaries are unknown, so even bytes that look like a valid frame are dropped.
		[Test]
		public void InputAfterAnInvalidSize_IsDiscarded()
		{
			var (streamer, received) = CreateStreamer();
			Assert.Throws<InvalidDataException>(() => streamer.PutReceivedBytes(HeaderOnly(0)));

			Assert.DoesNotThrow(() => streamer.PutReceivedBytes(Heartbeat.ToArray()));

			Assert.That(received, Is.Empty);
		}

		[Test]
		public void FramePassedOn_HasItsSizeAsItsLength()
		{
			var (streamer, received) = CreateStreamer();

			foreach (var read in Concat(Frames.ToArray()).Chunk(9))
				streamer.PutReceivedBytes(read);

			Assert.That(received.Select(f => (int)BinaryPrimitives.ReadUInt16LittleEndian(f)), Is.EqualTo(received.Select(f => f.Length)));
		}

		static (MessageStreamer Streamer, List<byte[]> Received) CreateStreamer()
		{
			var streamer = new MessageStreamer();
			var received = new List<byte[]>();
			// A frame from a single read borrows that read's buffer, so copy it as the session's decoder would read it.
			streamer.MessageBytesReceived += bytes => received.Add(bytes.ToArray());
			return (streamer, received);
		}

		static byte[] EncodeHeartbeat(long currentDateTime)
		{
			var message = new byte[HeartbeatSize];
			BinaryPrimitives.WriteUInt16LittleEndian(message, HeartbeatSize);
			BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(2), HeartbeatType);
			BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(8), currentDateTime);
			return message;
		}

		/// <summary>A heartbeat's Size/Type header with the given Size: what a faulty or hostile client might send.</summary>
		static byte[] HeaderOnly(int size)
		{
			var header = new byte[4];
			BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)size);
			BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), HeartbeatType);
			return header;
		}

		static byte[] Concat(IEnumerable<byte[]> frames) => frames.SelectMany(f => f).ToArray();
	}
}
