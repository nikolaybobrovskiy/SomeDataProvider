// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;

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

		/// <summary>
		/// Decodes a string of the request, keeping at most its first <see cref="MaxStringLength"/> bytes, as the server
		/// always has. This also keeps any reply that quotes the string within the maximum message size.
		/// </summary>
		static string GetString(ReadOnlySpan<byte> frame, s_VariableLengthStringField field)
		{
			var bytes = DtcVlsDecoder.GetBytes(frame, field);
			return MessageProtocol.TextEncoding.GetString(bytes.Length > MaxStringLength ? bytes[..MaxStringLength] : bytes);
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