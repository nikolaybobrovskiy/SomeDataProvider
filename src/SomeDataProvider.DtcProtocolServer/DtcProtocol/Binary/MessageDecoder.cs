// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;

	using Dtc.Protocol;
	using Dtc.Protocol.Private;

	using LogonRequestRecord = Dtc.Protocol.Messages.LogonRequest;

	class MessageDecoder : IMessageDecoder
	{
		protected MessageDecoder(Memory<byte> buffer)
		{
			Buffer = buffer;
		}

		/// <summary>One complete message, as <see cref="MessageStreamer"/> passes it on: its Size field equals its length.</summary>
		protected Memory<byte> Buffer { get; }

		public ushort DecodeMessageType()
		{
			return BitConverter.ToUInt16(Buffer.Span.Slice(2, 2));
		}

		public virtual LogonRequest DecodeLogonRequest()
		{
			DtcBinaryDecoder.Decode(Buffer.Span, out s_LogonRequest message);
			var r = LogonRequestRecord.FromBinary(in message, MessageProtocol.TextEncoding);
			return new LogonRequest(r.HeartbeatIntervalInSeconds, r.ClientName, r.HardwareIdentifier);
		}

		public virtual HistoricalPriceDataRequest DecodeHistoricalPriceDataRequest()
		{
			throw new NotImplementedException();
		}

		public virtual MarketDataRequest DecodeMarketDataRequest()
		{
			throw new NotImplementedException();
		}

		public virtual SecurityDefinitionForSymbolRequest DecodeSecurityDefinitionForSymbolRequest()
		{
			throw new NotImplementedException();
		}

		public virtual SymbolSearchRequest DecodeSymbolSearchRequest()
		{
			throw new NotImplementedException();
		}

		public virtual SeriesExtensionRequest DecodeSeriesExtensionRequest()
		{
			throw NewSeriesExtensionNotSupportedException();
		}

		public virtual SeriesMetadataRequest DecodeSeriesMetadataRequest()
		{
			throw NewSeriesExtensionNotSupportedException();
		}

		public virtual SeriesVintagesRequest DecodeSeriesVintagesRequest()
		{
			throw NewSeriesExtensionNotSupportedException();
		}

		/// <summary>Decodes ENCODING_REQUEST, which has no strings, so every encoding sends it in binary encoding.</summary>
		public s_EncodingRequest DecodeEncodingRequest()
		{
			DtcBinaryDecoder.Decode(Buffer.Span, out s_EncodingRequest message);
			return message;
		}

		/// <summary>The series extension's messages exist only in binary encoding with variable-length strings.</summary>
		internal static NotSupportedException NewSeriesExtensionNotSupportedException() =>
			new("The series extension's messages exist only in binary encoding with variable-length strings.");

		public sealed class Factory : IMessageDecoderFactory
		{
			public IMessageDecoder CreateMessageDecoder(Memory<byte> buffer)
			{
				return new MessageDecoder(buffer);
			}
		}
	}
}