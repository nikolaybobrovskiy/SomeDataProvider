// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;

	using NBLib.BuiltInTypes;
	using NBLib.DateTime;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	class MessageEncoder : IMessageEncoder
	{
		byte[]? _bytes;

		protected byte[]? Bytes
		{
			get => _bytes;
			set
			{
				if (value == null || value.Length == 0)
				{
					return;
				}
				if (_bytes?.Length > 0)
				{
					var newArr = new byte[_bytes.Length + value.Length];
					Array.Copy(_bytes, newArr, _bytes.Length);
					Array.Copy(value, 0, newArr, _bytes.Length, value.Length);
					_bytes = newArr;
				}
				else
				{
					_bytes = value;
				}
			}
		}

		public void EncodeEncodingResponse(EncodingEnum encoding)
		{
			Bytes = StructConverter.StructToBytesArray(new EncodingResponse(encoding));
		}

		public virtual void EncodeLogonResponse(LogonStatusEnum logonStatus, string resultText, bool oneHistoricalPriceDataRequestPerConnection)
		{
			throw new NotImplementedException();
		}

		public void EncodeHeartbeatMessage(uint numDroppedMessages)
		{
			Bytes = StructConverter.StructToBytesArray(new Heartbeat(0));
		}

		public virtual void EncodeHistoricalPriceDataReject(int requestId, HistoricalPriceDataRejectReasonCodeEnum rejectReasonCode, string rejectText)
		{
			throw new NotImplementedException();
		}

		public virtual void EncodeMarketDataReject(uint symbolId, string rejectText)
		{
			throw new NotImplementedException();
		}

		public void EncodeMarketDataSnapshot(uint symbolId, TradingStatusEnum tradingStatus, DateTime lastTradeDateTime)
		{
			Bytes = StructConverter.StructToBytesArray(new MarketDataSnapshot(
				symbolId,
				tradingStatus)
			{
				LastTradeDateTime = lastTradeDateTime.ToUnixTimeStamp(),
			});
		}

		public virtual void EncodeSecurityDefinitionReject(int requestId, string rejectText)
		{
			throw new NotImplementedException();
		}

		public virtual void EncodeSecurityDefinitionResponse(int requestId, bool isFinalMessage, string? symbol, string? exchange, SecurityTypeEnum securityType, string? description, PriceDisplayFormatEnum priceDisplayFormat, string? currency, bool isDelayed)
		{
			throw new NotImplementedException();
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