namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;

	class MessageDecoder : Binary.MessageDecoder
	{
		MessageDecoder(byte[] buffer, in long offset, in long size)
			: base(buffer, in offset, in size)
		{
		}

		public override LogonRequest DecodeLogonRequest()
		{
			throw new NotImplementedException();
		}

		public new sealed class Factory : IMessageDecoderFactory
		{
			public IMessageDecoder CreateMessageDecoder(byte[] buffer, long offset, long size)
			{
				return new MessageDecoder(buffer, offset, size);
			}
		}
	}
}