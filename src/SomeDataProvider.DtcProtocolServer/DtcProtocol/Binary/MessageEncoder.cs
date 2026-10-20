// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;
	using System.Collections.Generic;

	using Dtc.Protocol;
	using Dtc.Protocol.Private;

	using NBLib.DateTime;

	using SomeDataProvider.DtcProtocolServer.Data;

	using EncodeSecurityDefinitionResponseArgs = SomeDataProvider.DtcProtocolServer.DtcProtocol.IMessageEncoder.EncodeSecurityDefinitionResponseArgs;

	/// <summary>Encodes messages in binary encoding with the structures and encoder of Dtc.Protocol.</summary>
	/// <remarks>
	/// The encoder appends each message it encodes to its buffer, so <see cref="GetEncodedMessage"/> returns every message
	/// encoded so far, one after another.
	/// </remarks>
	class MessageEncoder : IMessageEncoder
	{
		byte[]? _bytes;

		public int EncodedMessageSize => _bytes?.Length ?? 0;

		public void EncodeEncodingResponse(EncodingEnum encoding)
		{
			Append(new s_EncodingResponse { Encoding = encoding });
		}

		public virtual void EncodeLogonResponse(LogonStatusEnum logonStatus, string resultText, bool oneHistoricalPriceDataRequestPerConnection)
		{
			throw new NotImplementedException();
		}

		public void EncodeHeartbeatMessage(uint numDroppedMessages)
		{
			Append(new s_Heartbeat { NumDroppedMessages = numDroppedMessages });
		}

		public virtual void EncodeHistoricalPriceDataReject(int requestId, HistoricalPriceDataRejectReasonCodeEnum rejectReasonCode, string rejectText)
		{
			throw new NotImplementedException();
		}

		public virtual void EncodeMarketDataReject(uint symbolId, string rejectText)
		{
			throw new NotImplementedException();
		}

		public void EncodeMarketDataSnapshot(uint symbolId, TradingStatusEnum tradingStatus, DateTime lastTradeDateTime)
		{
			Append(new s_MarketDataSnapshot
			{
				SymbolID = symbolId,
				TradingStatus = tradingStatus,
				LastTradeDateTime = lastTradeDateTime.ToUnixTimeStamp(),
			});
		}

		public virtual void EncodeSecurityDefinitionReject(int requestId, string rejectText)
		{
			throw new NotImplementedException();
		}

		public virtual void EncodeSecurityDefinitionResponse(EncodeSecurityDefinitionResponseArgs args)
		{
			throw new NotImplementedException();
		}

		public void EncodeHistoricalPriceDataResponseHeader(int requestId, HistoricalDataIntervalEnum recordInterval, bool useZLibCompression, bool noRecordsToReturn, float intToFloatPriceDivisor)
		{
			Append(new s_HistoricalPriceDataResponseHeader
			{
				RequestID = requestId,
				RecordInterval = recordInterval,
				UseZLibCompression = useZLibCompression ? (byte)1 : (byte)0,
				NoRecordsToReturn = noRecordsToReturn ? (byte)1 : (byte)0,
				IntToFloatPriceDivisor = intToFloatPriceDivisor,
			});
		}

		public void EncodeHistoricalPriceDataRecordResponse(int requestId, DateTime startDateTime, double openPrice, double highPrice, double lowPrice, double lastPrice, double volume, uint openInterestOrNumTrades, double bidVolume, double askVolume, bool isFinalRecord)
		{
			Append(new s_HistoricalPriceDataRecordResponse
			{
				RequestID = requestId,
				StartDateTime = DtcDateTime.ToUnixMicroseconds(startDateTime),
				OpenPrice = openPrice,
				HighPrice = highPrice,
				LowPrice = lowPrice,
				LastPrice = lastPrice,
				Volume = volume,
				// OpenInterest and NumTrades share their storage, as in the header's union.
				OpenInterest = openInterestOrNumTrades,
				BidVolume = bidVolume,
				AskVolume = askVolume,
				IsFinalRecord = isFinalRecord ? (byte)1 : (byte)0,
			});
		}

		public virtual void EncodeSeriesExtensionResponse(SeriesExtensionResponse response)
		{
			throw MessageDecoder.NewSeriesExtensionNotSupportedException();
		}

		public virtual void EncodeSeriesMetadataResponse(SeriesMetadataResponse response)
		{
			throw MessageDecoder.NewSeriesExtensionNotSupportedException();
		}

		public virtual void EncodeSeriesVintageRecords(int requestId, IReadOnlyList<SeriesVintage> rows, int start, int count)
		{
			throw MessageDecoder.NewSeriesExtensionNotSupportedException();
		}

		public virtual void EncodeSeriesVintagesTerminator(int requestId)
		{
			throw MessageDecoder.NewSeriesExtensionNotSupportedException();
		}

		public virtual void EncodeSeriesRequestReject(SeriesRequestReject reject)
		{
			throw MessageDecoder.NewSeriesExtensionNotSupportedException();
		}

		public byte[] GetEncodedMessage()
		{
			return _bytes ?? throw new InvalidOperationException("Message is not ready.");
		}

		/// <summary>Encodes a message without strings and appends it to the buffer.</summary>
		protected void Append<T>(in T message)
			where T : unmanaged, IDtcBinaryMessage
		{
			DtcBinaryEncoder.Encode(in message, Extend(DtcBinaryEncoder.GetEncodedLength<T>()));
		}

		/// <summary>Appends an encoded message to the buffer.</summary>
		protected void Append(ReadOnlySpan<byte> message)
		{
			message.CopyTo(Extend(message.Length));
		}

		/// <summary>Grows the buffer by <paramref name="length"/> bytes and returns them.</summary>
		protected Span<byte> Extend(int length)
		{
			var start = _bytes?.Length ?? 0;
			Array.Resize(ref _bytes, start + length);
			return _bytes.AsSpan(start, length);
		}

		public sealed class Factory : IMessageEncoderFactory
		{
			public IMessageEncoder CreateMessageEncoder()
			{
				return new MessageEncoder();
			}
		}
	}
}