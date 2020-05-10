namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	class MessageEncoder : Binary.MessageEncoder
	{
		public new sealed class Factory : IMessageEncoderFactory
		{
			public IMessageEncoder CreateMessageEncoder()
			{
				return new Binary.MessageEncoder();
			}
		}
	}
}