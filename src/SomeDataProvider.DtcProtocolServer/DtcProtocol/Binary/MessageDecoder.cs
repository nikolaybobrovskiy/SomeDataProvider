// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;

	using NBLib.BuiltInTypes;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class MessageDecoder : IMessageDecoder
	{
		protected MessageDecoder(byte[] buffer, int offset, int size)
		{
			Buffer = buffer;
			Offset = offset;
			Size = size;
		}

		protected ReadOnlySpan<byte> BufferSpan => Buffer.AsSpan(Offset, Size);

		protected byte[] Buffer { get; }

		protected int Offset { get; }

		protected int Size { get; }

		public MessageTypeEnum DecodeMessageType()
		{
			return (MessageTypeEnum)BitConverter.ToUInt16(BufferSpan.Slice(2, 2));
		}

		public virtual DtcProtocol.LogonRequest DecodeLogonRequest()
		{
			var r = StructConverter.BytesArrayToStruct<LogonRequest>(Buffer, Offset);
			return new DtcProtocol.LogonRequest(r.HeartbeatIntervalInSeconds, r.ClientName, r.HardwareIdentifier);
		}

		public virtual DtcProtocol.HistoricalPriceDataRequest DecodeHistoricalPriceDataRequest()
		{
			throw new NotImplementedException();
		}

		public EncodingRequest DecodeEncodingRequest()
		{
			return StructConverter.BytesArrayToStruct<EncodingRequest>(Buffer, Offset);
		}

		public sealed class Factory : IMessageDecoderFactory
		{
			public IMessageDecoder CreateMessageDecoder(byte[] buffer, int offset, int size)
			{
				return new MessageDecoder(buffer, offset, size);
			}
		}
	}
}