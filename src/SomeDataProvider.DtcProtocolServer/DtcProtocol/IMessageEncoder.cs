namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	interface IMessageEncoder
	{
		void EncodeEncodingResponse(EncodingEnum encoding);

		byte[] GetEncodedMessage();
	}

	interface IMessageEncoderFactory
	{
		IMessageEncoder CreateMessageEncoder();
	}
}