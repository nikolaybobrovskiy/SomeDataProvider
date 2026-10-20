// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Buffers.Binary;
	using System.Collections.Generic;
	using System.Runtime.CompilerServices;
	using System.Text;

	using Dtc.Protocol.Private;
	using Dtc.Protocol.Vls;

	using NUnit.Framework;

	/// <summary>
	/// Encodes the series extension's requests and decodes its answers with the shared definitions of Dtc.Protocol
	/// (<c>Dtc.Protocol.Private</c>), as the platform's client does, and checks each answer's Type and BaseSize.
	/// </summary>
	static class SeriesWire
	{
		public static readonly Encoding Utf8 = Encoding.UTF8;

		public static byte[] Encode(SeriesExtensionRequest request) => ToVls(buffer => request.ToVls(buffer, Utf8));

		public static byte[] Encode(SeriesMetadataRequest request) => ToVls(buffer => request.ToVls(buffer, Utf8));

		public static byte[] Encode(SeriesVintagesRequest request) => ToVls(buffer => request.ToVls(buffer, Utf8));

		public static ushort TypeOf(byte[] frame) => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2));

		public static SeriesExtensionResponse DecodeExtensionResponse(byte[] frame)
		{
			Assert.That(TypeOf(frame), Is.EqualTo(SeriesExtensionResponse.MessageType), "Type");
			DtcVlsDecoder.Decode(frame, out s_SeriesExtensionResponse message);
			Assert.That(message.BaseSize, Is.EqualTo(Unsafe.SizeOf<s_SeriesExtensionResponse>()), "BaseSize");
			return SeriesExtensionResponse.FromVls(in message, frame, Utf8);
		}

		public static SeriesMetadataResponse DecodeMetadataResponse(byte[] frame)
		{
			Assert.That(TypeOf(frame), Is.EqualTo(SeriesMetadataResponse.MessageType), "Type");
			DtcVlsDecoder.Decode(frame, out s_SeriesMetadataResponse message);
			Assert.That(message.BaseSize, Is.EqualTo(Unsafe.SizeOf<s_SeriesMetadataResponse>()), "BaseSize");
			return SeriesMetadataResponse.FromVls(in message, frame, Utf8);
		}

		public static SeriesVintageRecord DecodeVintageRecord(byte[] frame)
		{
			Assert.That(TypeOf(frame), Is.EqualTo(SeriesVintageRecord.MessageType), "Type");
			DtcVlsDecoder.Decode(frame, out s_SeriesVintageRecord message);
			Assert.That(message.BaseSize, Is.EqualTo(Unsafe.SizeOf<s_SeriesVintageRecord>()), "BaseSize");
			Assert.That(frame, Has.Length.EqualTo(Unsafe.SizeOf<s_SeriesVintageRecord>()), "Size");
			return SeriesVintageRecord.FromVls(in message, frame, Utf8);
		}

		public static SeriesRequestReject DecodeReject(byte[] frame)
		{
			Assert.That(TypeOf(frame), Is.EqualTo(SeriesRequestReject.MessageType), "Type");
			DtcVlsDecoder.Decode(frame, out s_SeriesRequestReject message);
			Assert.That(message.BaseSize, Is.EqualTo(Unsafe.SizeOf<s_SeriesRequestReject>()), "BaseSize");
			return SeriesRequestReject.FromVls(in message, frame, Utf8);
		}

		/// <summary>
		/// Decodes the answer to a vintages request, which must be rows and exactly one terminator, last, or exactly one
		/// reject, last; every message carries <paramref name="requestId"/>.
		/// </summary>
		public static VintagesAnswer DecodeVintagesAnswer(IReadOnlyList<byte[]> frames, int requestId)
		{
			Assert.That(frames, Is.Not.Empty, "The answer has no message.");
			var rows = new List<SeriesVintageRecord>();
			for (var i = 0; i < frames.Count - 1; i++)
			{
				var row = DecodeVintageRecord(frames[i]);
				Assert.That(row.RequestID, Is.EqualTo(requestId), "A row's RequestID.");
				Assert.That(row.IsFinalMessage, Is.False, "A row before the end has IsFinalMessage set.");
				rows.Add(row);
			}
			var last = frames[^1];
			if (TypeOf(last) == SeriesRequestReject.MessageType)
			{
				var reject = DecodeReject(last);
				Assert.That(reject.RequestID, Is.EqualTo(requestId), "The reject's RequestID.");
				return new VintagesAnswer(rows, reject);
			}
			var terminator = DecodeVintageRecord(last);
			Assert.That(terminator.RequestID, Is.EqualTo(requestId), "The terminator's RequestID.");
			Assert.That(terminator.IsFinalMessage, Is.True, "The answer does not end with the terminator.");
			return new VintagesAnswer(rows, null);
		}

		/// <summary>Whether a frame ends the answer to a vintages request: a reject, or a terminator.</summary>
		public static bool EndsVintagesAnswer(byte[] frame) =>
			TypeOf(frame) == SeriesRequestReject.MessageType || (TypeOf(frame) == SeriesVintageRecord.MessageType && DecodeVintageRecord(frame).IsFinalMessage);

		static byte[] ToVls(Func<byte[], int> encode)
		{
			var buffer = new byte[ushort.MaxValue];
			return buffer.AsSpan(0, encode(buffer)).ToArray();
		}
	}

	/// <summary>The decoded answer to a vintages request: its rows, and the reject that ended it, if any.</summary>
	sealed record VintagesAnswer(IReadOnlyList<SeriesVintageRecord> Rows, SeriesRequestReject? Reject);
}
