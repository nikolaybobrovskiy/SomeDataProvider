namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	interface IMessageEncoder
	{
		void EncodeEncodingResponse(in EncodingResponse encodingResponse);

		byte[] GetEncodedMessage();
	}

	interface IMessageEncoderFactory
	{
		IMessageEncoder CreateMessageEncoder();
	}
}