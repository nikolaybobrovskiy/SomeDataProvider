// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;

	using NBLib.BuiltInTypes;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class MessageDecoder : IMessageDecoder
	{
		protected MessageDecoder(Memory<byte> buffer)
		{
			Buffer = buffer;
		}

		protected Memory<byte> Buffer { get; }

		public MessageTypeEnum DecodeMessageType()
		{
			return (MessageTypeEnum)BitConverter.ToUInt16(Buffer.Span.Slice(2, 2));
		}

		public virtual DtcProtocol.LogonRequest DecodeLogonRequest()
		{
			var r = GetRequest<LogonRequest>();
			return new DtcProtocol.LogonRequest(r.HeartbeatIntervalInSeconds, r.ClientName, r.HardwareIdentifier);
		}

		public virtual DtcProtocol.HistoricalPriceDataRequest DecodeHistoricalPriceDataRequest()
		{
			throw new NotImplementedException();
		}

		// ReSharper disable once RedundantNameQualifier
		public virtual DtcProtocol.MarketDataRequest DecodeMarketDataRequest()
		{
			throw new NotImplementedException();
		}

		// ReSharper disable once RedundantNameQualifier
		public virtual DtcProtocol.SecurityDefinitionForSymbolRequest DecodeSecurityDefinitionForSymbolRequest()
		{
			throw new NotImplementedException();
		}

		public EncodingRequest DecodeEncodingRequest()
		{
			return GetRequest<EncodingRequest>();
		}

		protected T GetRequest<T>()
			where T : struct
		{
			return Buffer.ToStruct<T>();
		}

		public sealed class Factory : IMessageDecoderFactory
		{
			public IMessageDecoder CreateMessageDecoder(Memory<byte> buffer)
			{
				return new MessageDecoder(buffer);
			}
		}
	}
}