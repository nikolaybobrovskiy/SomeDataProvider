// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using NBLib.BuiltInTypes;

	class MessageDecoder : Binary.MessageDecoder
	{
		MessageDecoder(byte[] buffer, int offset, int size)
			: base(buffer, offset, size)
		{
		}

		public override DtcProtocol.LogonRequest DecodeLogonRequest()
		{
			var r = StructConverter.BytesArrayToStruct<LogonRequest>(Buffer, Offset);
			return new DtcProtocol.LogonRequest(
				r.HeartbeatIntervalInSeconds,
				r.GetClientName(BufferSpan),
				r.GetHardwareIdentifier(BufferSpan));
		}

		public override DtcProtocol.HistoricalPriceDataRequest DecodeHistoricalPriceDataRequest()
		{
			var r = StructConverter.BytesArrayToStruct<HistoricalPriceDataRequest>(Buffer, Offset);
			return new DtcProtocol.HistoricalPriceDataRequest(
				r.RequestId,
				r.GetSymbol(BufferSpan),
				r.GetExchange(BufferSpan),
				r.UseZLibCompression == 1);
		}

		public new sealed class Factory : IMessageDecoderFactory
		{
			public IMessageDecoder CreateMessageDecoder(byte[] buffer, int offset, int size)
			{
				return new MessageDecoder(buffer, offset, size);
			}
		}
	}
}