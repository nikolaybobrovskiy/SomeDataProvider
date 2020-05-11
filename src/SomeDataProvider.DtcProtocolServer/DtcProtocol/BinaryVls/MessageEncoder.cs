// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using NBLib.BuiltInTypes;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class MessageEncoder : Binary.MessageEncoder
	{
		public override void EncodeLogonResponse(LogonStatusEnum logonStatus, string resultText)
		{
			var logonResponse = new LogonResponse(logonStatus);
			var bytes = new byte[logonResponse.BaseSize + resultText.GetVlsFieldLength()];
			logonResponse.SetResultText(resultText, bytes);
			Bytes = StructConverter.StructToBytesArray(logonResponse, bytes);
		}

		public new sealed class Factory : IMessageEncoderFactory
		{
			public IMessageEncoder CreateMessageEncoder()
			{
				return new MessageEncoder();
			}
		}
	}
}