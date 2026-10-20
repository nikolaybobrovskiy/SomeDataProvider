// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Runtime.CompilerServices;

	using Dtc.Protocol;
	using Dtc.Protocol.Private;
	using Dtc.Protocol.Vls;

	using SomeDataProvider.DtcProtocolServer.Data;

	using EncodeSecurityDefinitionResponseArgs = SomeDataProvider.DtcProtocolServer.DtcProtocol.IMessageEncoder.EncodeSecurityDefinitionResponseArgs;
	using VlsHistoricalPriceDataReject = Dtc.Protocol.Vls.s_HistoricalPriceDataReject;
	using VlsLogonResponse = Dtc.Protocol.Vls.s_LogonResponse;
	using VlsMarketDataReject = Dtc.Protocol.Vls.s_MarketDataReject;
	using VlsSecurityDefinitionReject = Dtc.Protocol.Vls.s_SecurityDefinitionReject;
	using VlsSecurityDefinitionResponse = Dtc.Protocol.Vls.s_SecurityDefinitionResponse;

	/// <summary>
	/// Encodes messages in binary encoding with variable-length strings, with the structures and codec of Dtc.Protocol.
	/// Messages without strings are sent as in binary encoding.
	/// </summary>
	class MessageEncoder : Binary.MessageEncoder
	{
		public override void EncodeLogonResponse(LogonStatusEnum logonStatus, string resultText, bool oneHistoricalPriceDataRequestPerConnection)
		{
			var message = new VlsLogonResponse
			{
				Result = logonStatus,
				// The header's defaults advertise order cancel/replace and market depth, which this server does not support.
				OrderCancelReplaceSupported = 0,
				MarketDepthIsSupported = 0,
				SecurityDefinitionsSupported = 1,
				HistoricalPriceDataSupported = 1,
				ResubscribeWhenMarketDataFeedAvailable = 1,
				MarketDataSupported = 1,
				OneHistoricalPriceDataRequestPerConnection = oneHistoricalPriceDataRequestPerConnection ? (byte)1 : (byte)0,
			};
			var buffer = NewBuffer<VlsLogonResponse>(resultText);
			var encoder = new DtcVlsEncoder<VlsLogonResponse>(ref message, buffer);
			encoder.AddString(ref message.ResultText, resultText, MessageProtocol.TextEncoding);
			Append(buffer.AsSpan(0, encoder.Complete()));
		}

		public override void EncodeHistoricalPriceDataReject(int requestId, HistoricalPriceDataRejectReasonCodeEnum rejectReasonCode, string rejectText)
		{
			var message = new VlsHistoricalPriceDataReject
			{
				RequestID = requestId,
				RejectReasonCode = rejectReasonCode,
			};
			var buffer = NewBuffer<VlsHistoricalPriceDataReject>(rejectText);
			var encoder = new DtcVlsEncoder<VlsHistoricalPriceDataReject>(ref message, buffer);
			encoder.AddString(ref message.RejectText, rejectText, MessageProtocol.TextEncoding);
			Append(buffer.AsSpan(0, encoder.Complete()));
		}

		public override void EncodeMarketDataReject(uint symbolId, string rejectText)
		{
			var message = new VlsMarketDataReject { SymbolID = symbolId };
			var buffer = NewBuffer<VlsMarketDataReject>(rejectText);
			var encoder = new DtcVlsEncoder<VlsMarketDataReject>(ref message, buffer);
			encoder.AddString(ref message.RejectText, rejectText, MessageProtocol.TextEncoding);
			Append(buffer.AsSpan(0, encoder.Complete()));
		}

		public override void EncodeSecurityDefinitionReject(int requestId, string rejectText)
		{
			var message = new VlsSecurityDefinitionReject { RequestID = requestId };
			var buffer = NewBuffer<VlsSecurityDefinitionReject>(rejectText);
			var encoder = new DtcVlsEncoder<VlsSecurityDefinitionReject>(ref message, buffer);
			encoder.AddString(ref message.RejectText, rejectText, MessageProtocol.TextEncoding);
			Append(buffer.AsSpan(0, encoder.Complete()));
		}

		public override void EncodeSecurityDefinitionResponse(EncodeSecurityDefinitionResponseArgs args)
		{
			// SecurityType stays unset: the server has never sent the symbol's type.
			var message = new VlsSecurityDefinitionResponse
			{
				RequestID = args.RequestId,
				MinPriceIncrement = args.MinPriceIncrement,
				PriceDisplayFormat = args.PriceDisplayFormat,
				IsFinalMessage = args.IsFinalMessage ? (byte)1 : (byte)0,
				// The header's default says the symbol has market depth data, which this server does not send.
				HasMarketDepthData = 0,
				IsDelayed = args.IsDelayed ? (byte)1 : (byte)0,
			};
			var buffer = NewBuffer<VlsSecurityDefinitionResponse>(args.Symbol, args.Exchange, args.Description, args.Currency);
			var encoder = new DtcVlsEncoder<VlsSecurityDefinitionResponse>(ref message, buffer);
			var textEncoding = MessageProtocol.TextEncoding;
			encoder.AddString(ref message.Symbol, args.Symbol, textEncoding);
			encoder.AddString(ref message.Exchange, args.Exchange, textEncoding);
			encoder.AddString(ref message.Description, args.Description, textEncoding);
			encoder.AddString(ref message.Currency, args.Currency, textEncoding);
			Append(buffer.AsSpan(0, encoder.Complete()));
		}

		public override void EncodeSeriesExtensionResponse(SeriesExtensionResponse response)
		{
			ArgumentNullException.ThrowIfNull(response);
			var buffer = NewSeriesBuffer<s_SeriesExtensionResponse>();
			Append(buffer.AsSpan(0, response.ToVls(buffer, MessageProtocol.SeriesTextEncoding)));
		}

		public override void EncodeSeriesMetadataResponse(SeriesMetadataResponse response)
		{
			ArgumentNullException.ThrowIfNull(response);
			var buffer = NewSeriesBuffer<s_SeriesMetadataResponse>(response.Symbol, response.Exchange, response.Title, response.Units, response.UnitsShort);
			Append(buffer.AsSpan(0, response.ToVls(buffer, MessageProtocol.SeriesTextEncoding)));
		}

		public override void EncodeSeriesVintageRecords(int requestId, IReadOnlyList<SeriesVintage> rows, int start, int count)
		{
			ArgumentNullException.ThrowIfNull(rows);
			ArgumentOutOfRangeException.ThrowIfNegative(start);
			ArgumentOutOfRangeException.ThrowIfNegative(count);
			ArgumentOutOfRangeException.ThrowIfGreaterThan(start, rows.Count - count);
			// Rows have no strings, so each takes exactly the fixed part; the buffer grows once for all of them.
			var size = Unsafe.SizeOf<s_SeriesVintageRecord>();
			var destination = Extend(count * size);
			for (var i = 0; i < count; i++)
			{
				var row = rows[start + i];
				var record = new SeriesVintageRecord
				{
					RequestID = requestId,
					ObservationDate = row.ObservationDate,
					RealtimeStart = row.RealtimeStart,
					RealtimeEnd = row.RealtimeEnd,
					Value = row.Value,
				};
				var length = record.ToVls(destination.Slice(i * size, size), MessageProtocol.SeriesTextEncoding);
				Debug.Assert(length == size, "A SERIES_VINTAGE_RECORD has no strings, so its length is its fixed part.");
			}
		}

		public override void EncodeSeriesVintagesTerminator(int requestId)
		{
			var buffer = NewSeriesBuffer<s_SeriesVintageRecord>();
			var terminator = new SeriesVintageRecord { RequestID = requestId, IsFinalMessage = true };
			Append(buffer.AsSpan(0, terminator.ToVls(buffer, MessageProtocol.SeriesTextEncoding)));
		}

		public override void EncodeSeriesRequestReject(SeriesRequestReject reject)
		{
			ArgumentNullException.ThrowIfNull(reject);
			var buffer = NewSeriesBuffer<s_SeriesRequestReject>(reject.RejectText);
			Append(buffer.AsSpan(0, reject.ToVls(buffer, MessageProtocol.SeriesTextEncoding)));
		}

		/// <summary>Allocates storage for a message of type <typeparamref name="T"/> with these strings.</summary>
		static byte[] NewBuffer<T>(params ReadOnlySpan<string?> strings)
			where T : unmanaged, IDtcVlsMessage<T>
		{
			var length = Unsafe.SizeOf<T>();
			foreach (var s in strings)
			{
				// An empty string is absent; any other takes its bytes and a null terminator.
				if (!string.IsNullOrEmpty(s))
					length += MessageProtocol.TextEncoding.GetByteCount(s) + 1;
			}
			return new byte[length];
		}

		/// <summary>Allocates storage for a series extension message of type <typeparamref name="T"/> with these UTF-8 strings.</summary>
		static byte[] NewSeriesBuffer<T>(params ReadOnlySpan<string?> strings)
			where T : unmanaged, IDtcVlsMessage<T>
		{
			var length = Unsafe.SizeOf<T>();
			foreach (var s in strings)
			{
				// An empty string is absent; any other takes its bytes and a null terminator.
				if (!string.IsNullOrEmpty(s))
					length += MessageProtocol.SeriesTextEncoding.GetByteCount(s) + 1;
			}
			return new byte[length];
		}

		public new sealed class Factory : IMessageEncoderFactory
		{
			public IMessageEncoder CreateMessageEncoder()
			{
				return new MessageEncoder();
			}
		}
	}
}