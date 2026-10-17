// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Buffers.Binary;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;

	using NUnit.Framework;

	/// <summary>
	/// Encodes requests and decodes responses at the byte offsets of the DTC headers (DTCProtocol.h and
	/// DTCProtocolVLS.h, protocol version 8), independently of the server's own structs.
	/// </summary>
	static class DtcWire
	{
		public const ushort SecurityDefinitionForSymbolRequestType = 506;
		public const ushort SecurityDefinitionResponseType = 507;
		public const ushort SecurityDefinitionRejectType = 509;
		public const ushort HistoricalPriceDataRequestType = 800;
		public const ushort HistoricalPriceDataResponseHeaderType = 801;
		public const ushort HistoricalPriceDataRecordResponseType = 803;
		public const int IntervalOneDay = 86400;

		public static ushort TypeOf(byte[] frame) => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2));

		/// <summary>Splits bytes into frames by each frame's Size field, and checks that no partial frame remains.</summary>
		public static List<byte[]> SplitFrames(byte[] bytes)
		{
			var frames = new List<byte[]>();
			var offset = 0;
			while (offset < bytes.Length)
			{
				Assert.That(bytes.Length - offset, Is.GreaterThanOrEqualTo(4), "A partial frame header remains.");
				var size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
				Assert.That(size, Is.GreaterThanOrEqualTo(4), "A frame is shorter than its header.");
				Assert.That(offset + size, Is.LessThanOrEqualTo(bytes.Length), "A partial frame remains.");
				frames.Add(bytes.AsSpan(offset, size).ToArray());
				offset += size;
			}
			return frames;
		}

		// s_SecurityDefinitionForSymbolRequest (VLS): Size, Type, BaseSize, RequestID @8, Symbol @12, Exchange @16; 20 bytes.
		public static byte[] EncodeSecurityDefinitionForSymbolRequest(int requestId, string symbol, string exchange = "")
		{
			const int baseSize = 20;
			var message = NewVlsMessage(SecurityDefinitionForSymbolRequestType, baseSize, symbol, exchange, out var strings);
			BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(8), requestId);
			WriteVls(message, 12, strings[0]);
			WriteVls(message, 16, strings[1]);
			return message;
		}

		// s_HistoricalPriceDataRequest (VLS): RequestID @8, Symbol @12, Exchange @16, RecordInterval @20,
		// StartDateTime @24 and EndDateTime @32 (t_DateTime: seconds), MaxDaysToReturn @40, UseZLibCompression @44,
		// RequestDividendAdjustedStockData @45, Integer_1 @46, UseZLibNGCompression @48; 56 bytes.
		public static byte[] EncodeHistoricalPriceDataRequest(int requestId, string symbol, long startDateTime, long endDateTime, int recordInterval = IntervalOneDay)
		{
			const int baseSize = 56;
			var message = NewVlsMessage(HistoricalPriceDataRequestType, baseSize, symbol, string.Empty, out var strings);
			BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(8), requestId);
			WriteVls(message, 12, strings[0]);
			WriteVls(message, 16, strings[1]);
			BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(20), recordInterval);
			BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(24), startDateTime);
			BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(32), endDateTime);
			return message;
		}

		// s_SecurityDefinitionResponse (VLS): RequestID @8, Symbol @12 (vls_t: Offset, Length with the terminating 0).
		public static (int RequestId, string Symbol) DecodeSecurityDefinitionResponse(byte[] frame)
		{
			Assert.That(TypeOf(frame), Is.EqualTo(SecurityDefinitionResponseType));
			return (BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(8)), ReadVls(frame, 12));
		}

		// s_HistoricalPriceDataResponseHeader: RequestID @4, RecordInterval @8, UseZLibCompression @12, NoRecordsToReturn @13.
		public static HistoryHeader DecodeHistoryHeader(byte[] frame)
		{
			Assert.That(TypeOf(frame), Is.EqualTo(HistoricalPriceDataResponseHeaderType));
			Assert.That(frame.Length, Is.GreaterThanOrEqualTo(14));
			return new HistoryHeader(
				BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4)),
				BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(8)),
				frame[12],
				frame[13]);
		}

		// s_HistoricalPriceDataRecordResponse: RequestID @4, StartDateTime @8 (t_DateTimeWithMicrosecondsInt),
		// OpenPrice @16, HighPrice @24, LowPrice @32, LastPrice @40, Volume @48, IsFinalRecord @80; 88 bytes.
		public static HistoryRecord DecodeHistoryRecord(byte[] frame)
		{
			Assert.That(TypeOf(frame), Is.EqualTo(HistoricalPriceDataRecordResponseType));
			Assert.That(frame.Length, Is.EqualTo(88));
			return new HistoryRecord(
				BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4)),
				BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(8)),
				BinaryPrimitives.ReadDoubleLittleEndian(frame.AsSpan(40)),
				frame[80]);
		}

		/// <summary>Decodes a historical price data response and checks its structure.</summary>
		/// <remarks>
		/// The response has exactly one header, first. With NoRecordsToReturn set, nothing follows it. Otherwise records
		/// follow, and exactly one of them, the last, has IsFinalRecord set.
		/// </remarks>
		public static HistoryResponse DecodeHistoryResponse(IReadOnlyList<byte[]> frames)
		{
			Assert.That(frames, Is.Not.Empty, "The response has no message.");
			Assert.That(frames.Count(f => TypeOf(f) == HistoricalPriceDataResponseHeaderType), Is.EqualTo(1), "Header count.");
			var header = DecodeHistoryHeader(frames[0]);
			var records = frames.Skip(1).Select(DecodeHistoryRecord).ToList();
			if (header.NoRecordsToReturn != 0)
			{
				Assert.That(records, Is.Empty, "Records follow a header with NoRecordsToReturn set.");
			}
			else
			{
				Assert.That(records.Count(r => r.IsFinalRecord != 0), Is.EqualTo(1), "Final record count.");
				Assert.That(records[^1].IsFinalRecord, Is.EqualTo(1), "The last record is not the final record.");
			}
			return new HistoryResponse(header, records);
		}

		static byte[] NewVlsMessage(ushort type, int baseSize, string first, string second, out (ushort Offset, ushort Length)[] strings)
		{
			var firstBytes = ToVlsBytes(first);
			var secondBytes = ToVlsBytes(second);
			var message = new byte[baseSize + firstBytes.Length + secondBytes.Length];
			BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(0), (ushort)message.Length);
			BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(2), type);
			BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(4), (ushort)baseSize);
			firstBytes.CopyTo(message, baseSize);
			secondBytes.CopyTo(message, baseSize + firstBytes.Length);
			strings = new[]
			{
				(firstBytes.Length == 0 ? (ushort)0 : (ushort)baseSize, (ushort)firstBytes.Length),
				(secondBytes.Length == 0 ? (ushort)0 : (ushort)(baseSize + firstBytes.Length), (ushort)secondBytes.Length),
			};
			return message;
		}

		static byte[] ToVlsBytes(string value) =>
			value.Length == 0 ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(value + "\0");

		static void WriteVls(byte[] message, int fieldOffset, (ushort Offset, ushort Length) value)
		{
			BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(fieldOffset), value.Offset);
			BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(fieldOffset + 2), value.Length);
		}

		static string ReadVls(byte[] frame, int fieldOffset)
		{
			var offset = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(fieldOffset));
			var length = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(fieldOffset + 2));
			if (length == 0)
				return string.Empty;
			Assert.That(frame[offset + length - 1], Is.EqualTo(0), "A VLS string is not terminated by 0.");
			return Encoding.ASCII.GetString(frame, offset, length - 1);
		}
	}

	sealed record HistoryHeader(int RequestId, int RecordInterval, byte UseZLibCompression, byte NoRecordsToReturn);

	sealed record HistoryRecord(int RequestId, long StartDateTime, double LastPrice, byte IsFinalRecord);

	sealed record HistoryResponse(HistoryHeader Header, IReadOnlyList<HistoryRecord> Records);
}
