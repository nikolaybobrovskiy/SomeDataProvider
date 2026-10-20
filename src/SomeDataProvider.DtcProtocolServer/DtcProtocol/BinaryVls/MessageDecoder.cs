// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;
	using System.Buffers.Binary;
	using System.IO;

	using Dtc.Protocol.Private;
	using Dtc.Protocol.Vls;

	using NBLib.DateTime;

	/// <summary>Decodes requests in binary encoding with variable-length strings, with the codec of Dtc.Protocol.</summary>
	/// <remarks>
	/// A malformed string field, such as one that ends beyond the message or lacks its null terminator, makes the decoder
	/// throw <see cref="System.IO.InvalidDataException"/>.
	/// </remarks>
	class MessageDecoder : Binary.MessageDecoder
	{
		/// <summary>The most bytes of a request's string that the server decodes; it ignores the rest.</summary>
		const int MaxStringLength = 4096;

		MessageDecoder(Memory<byte> buffer)
			: base(buffer)
		{
		}

		public override LogonRequest DecodeLogonRequest()
		{
			var frame = Buffer.Span;
			DtcVlsDecoder.Decode(frame, out s_LogonRequest r);
			return new LogonRequest(
				r.HeartbeatIntervalInSeconds,
				GetString(frame, r.ClientName),
				GetString(frame, r.HardwareIdentifier));
		}

		public override HistoricalPriceDataRequest DecodeHistoricalPriceDataRequest()
		{
			var frame = Buffer.Span;
			DtcVlsDecoder.Decode(frame, out s_HistoricalPriceDataRequest r);
			return new HistoricalPriceDataRequest(
				r.RequestID,
				GetString(frame, r.Symbol),
				GetString(frame, r.Exchange),
				r.RecordInterval,
				Convert.ToDouble(r.StartDateTime).UnixTimeStampToDateTimeUtc(),
				Convert.ToDouble(r.EndDateTime).UnixTimeStampToDateTimeUtc(),
				r.MaxDaysToReturn,
				r.UseZLibCompression == 1);
		}

		public override MarketDataRequest DecodeMarketDataRequest()
		{
			var frame = Buffer.Span;
			DtcVlsDecoder.Decode(frame, out s_MarketDataRequest r);
			return new MarketDataRequest(
				r.RequestAction,
				r.SymbolID,
				GetString(frame, r.Symbol),
				GetString(frame, r.Exchange),
				r.IntervalForSnapshotUpdatesInMilliseconds);
		}

		public override SecurityDefinitionForSymbolRequest DecodeSecurityDefinitionForSymbolRequest()
		{
			var frame = Buffer.Span;
			DtcVlsDecoder.Decode(frame, out s_SecurityDefinitionForSymbolRequest r);
			return new SecurityDefinitionForSymbolRequest(
				r.RequestID,
				GetString(frame, r.Symbol),
				GetString(frame, r.Exchange));
		}

		public override SymbolSearchRequest DecodeSymbolSearchRequest()
		{
			var frame = Buffer.Span;
			DtcVlsDecoder.Decode(frame, out s_SymbolSearchRequest r);
			return new SymbolSearchRequest(
				r.RequestID,
				GetString(frame, r.SearchText),
				GetString(frame, r.Exchange),
				r.SecurityType,
				r.SearchType);
		}

		public override SeriesExtensionRequest DecodeSeriesExtensionRequest()
		{
			var frame = Buffer.Span;
			DtcVlsDecoder.Decode(frame, out s_SeriesExtensionRequest r);
			return SeriesExtensionRequest.FromVls(in r, frame, MessageProtocol.SeriesTextEncoding);
		}

		public override SeriesMetadataRequest DecodeSeriesMetadataRequest()
		{
			var frame = Buffer.Span;
			s_SeriesMetadataRequest r;
			try
			{
				DtcVlsDecoder.Decode(frame, out r);
				return SeriesMetadataRequest.FromVls(in r, frame, MessageProtocol.SeriesTextEncoding);
			}
			catch (InvalidDataException ex) when (TryReadSeriesRequestId(frame, out var requestId))
			{
				throw new InvalidSeriesRequestException(requestId, $"The request is malformed: {ex.Message}", ex);
			}
		}

		public override SeriesVintagesRequest DecodeSeriesVintagesRequest()
		{
			var frame = Buffer.Span;
			s_SeriesVintagesRequest r;
			SeriesVintagesRequest request;
			try
			{
				DtcVlsDecoder.Decode(frame, out r);
				request = SeriesVintagesRequest.FromVls(in r, frame, MessageProtocol.SeriesTextEncoding);
			}
			catch (InvalidDataException ex) when (TryReadSeriesRequestId(frame, out var requestId))
			{
				throw new InvalidSeriesRequestException(requestId, $"The request is malformed: {ex.Message}", ex);
			}
			// The record decodes an invalid date as null, which would widen the window to the earliest or latest available.
			CheckSeriesDate(r.RequestID, nameof(r.ObservationStart), r.ObservationStart, request.ObservationStart);
			CheckSeriesDate(r.RequestID, nameof(r.ObservationEnd), r.ObservationEnd, request.ObservationEnd);
			CheckSeriesDate(r.RequestID, nameof(r.RealtimeStart), r.RealtimeStart, request.RealtimeStart);
			CheckSeriesDate(r.RequestID, nameof(r.RealtimeEnd), r.RealtimeEnd, request.RealtimeEnd);
			return request;
		}

		/// <summary>
		/// Decodes a string of the request, keeping at most its first <see cref="MaxStringLength"/> bytes, as the server
		/// always has. This also keeps any reply that quotes the string within the maximum message size.
		/// </summary>
		static string GetString(ReadOnlySpan<byte> frame, s_VariableLengthStringField field)
		{
			var bytes = DtcVlsDecoder.GetBytes(frame, field);
			return MessageProtocol.TextEncoding.GetString(bytes.Length > MaxStringLength ? bytes[..MaxStringLength] : bytes);
		}

		/// <summary>
		/// Reads the RequestID of a series request whose decoding failed: it is at offset 8 of every request of the extension,
		/// within a fixed part of at least 12 bytes.
		/// </summary>
		static bool TryReadSeriesRequestId(ReadOnlySpan<byte> frame, out int requestId)
		{
			const int requestIdOffset = 8;
			const int requestIdEnd = requestIdOffset + sizeof(int);
			var readable = frame.Length >= requestIdEnd && BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]) >= requestIdEnd;
			requestId = readable ? BinaryPrimitives.ReadInt32LittleEndian(frame[requestIdOffset..]) : 0;
			return readable;
		}

		static void CheckSeriesDate(int requestId, string field, int wireValue, DateOnly? date)
		{
			if (wireValue != 0 && date == null)
				throw new InvalidSeriesRequestException(requestId, $"{field} {wireValue} is not a date: expected YYYYMMDD from 00010101 to 99991231, or 0.");
		}

		public new sealed class Factory : IMessageDecoderFactory
		{
			public IMessageDecoder CreateMessageDecoder(Memory<byte> buffer)
			{
				return new MessageDecoder(buffer);
			}
		}
	}
}