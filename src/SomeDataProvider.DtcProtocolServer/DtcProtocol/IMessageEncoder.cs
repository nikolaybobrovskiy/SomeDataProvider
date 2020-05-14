// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	interface IMessageEncoder
	{
		void EncodeEncodingResponse(EncodingEnum encoding);

		void EncodeLogonResponse(LogonStatusEnum logonStatus, string resultText, bool oneHistoricalPriceDataRequestPerConnection);

		void EncodeHeartbeatMessage(uint numDroppedMessages);

		void EncodeHistoricalPriceDataReject(int requestId, HistoricalPriceDataRejectReasonCodeEnum rejectReasonCode, string rejectText);

		byte[] GetEncodedMessage();
	}

	interface IMessageEncoderFactory
	{
		IMessageEncoder CreateMessageEncoder();
	}
}