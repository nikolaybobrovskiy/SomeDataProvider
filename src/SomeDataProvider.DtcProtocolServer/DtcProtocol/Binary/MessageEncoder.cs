// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;

	using NBLib.BuiltInTypes;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class MessageEncoder : IMessageEncoder
	{
		protected byte[]? Bytes { get; set; }

		public void EncodeEncodingResponse(EncodingEnum encoding)
		{
			Bytes = StructConverter.StructToBytesArray(new EncodingResponse(encoding));
		}

		public virtual void EncodeLogonResponse(LogonStatusEnum logonStatus, string resultText)
		{
			throw new NotImplementedException();
		}

		public void EncodeHeartbeatMessage(uint numDroppedMessages)
		{
			Bytes = StructConverter.StructToBytesArray(new Heartbeat(0));
		}

		public byte[] GetEncodedMessage()
		{
			return Bytes ?? throw new InvalidOperationException("Message is not ready.");
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