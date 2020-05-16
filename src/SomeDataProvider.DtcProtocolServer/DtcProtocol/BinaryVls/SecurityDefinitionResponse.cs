// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

// ReSharper disable FieldCanBeMadeReadOnly.Global

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.BinaryVls
{
	using System;
	using System.Runtime.InteropServices;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
	struct SecurityDefinitionResponse
	{
		public ushort Size;
		public MessageTypeEnum Type;
		public ushort BaseSize;
		public int RequestId;
		public VariableLengthStringField Symbol;
		public VariableLengthStringField Exchange;
		public SecurityTypeEnum SecurityType;
		public VariableLengthStringField Description;
		public float MinPriceIncrement;
		public PriceDisplayFormatEnum PriceDisplayFormat;
		public float CurrencyValuePerIncrement;
		public byte IsFinalMessage;
		public float FloatToIntPriceMultiplier;
		public float IntToFloatPriceDivisor;
		public VariableLengthStringField UnderlyingSymbol;
		public byte UpdatesBidAskOnly;
		public float StrikePrice;
		public PutCallEnum PutOrCall;
		public uint ShortInterest;
		public uint SecurityExpirationDate; // t_DateTime4Byte
		public float BuyRolloverInterest;
		public float SellRolloverInterest;
		public float EarningsPerShare;
		public uint SharesOutstanding;
		public float IntToFloatQuantityDivisor;
		public byte HasMarketDepthData;
		public float DisplayPriceMultiplier;
		public VariableLengthStringField ExchangeSymbol;
		public float InitialMarginRequirement;
		public float MaintenanceMarginRequirement;
		public VariableLengthStringField Currency;
		public float ContractSize;
		public uint OpenInterest;
		public uint RolloverDate; // t_DateTime4Byte
		public byte IsDelayed;

		public SecurityDefinitionResponse(int requestId, byte isFinalMessage)
			: this()
		{
			BaseSize = Size = Convert.ToUInt16(Marshal.SizeOf(this));
			Type = MessageTypeEnum.SecurityDefinitionResponse;
			RequestId = requestId;
			IsFinalMessage = isFinalMessage;
			FloatToIntPriceMultiplier = 1;
			IntToFloatPriceDivisor = 1;
		}

		public void SetSymbol(string? val, byte[] stringsBuffer)
		{
			Symbol = Symbol.CreateStringValue(val, stringsBuffer, Size);
			Size += Symbol.Length;
		}

		public void SetExchange(string? val, byte[] stringsBuffer)
		{
			Exchange = Exchange.CreateStringValue(val, stringsBuffer, Size);
			Size += Exchange.Length;
		}

		public void SetDescription(string? val, byte[] stringsBuffer)
		{
			Description = Description.CreateStringValue(val, stringsBuffer, Size);
			Size += Description.Length;
		}

		public void SetCurrency(string? val, byte[] stringsBuffer)
		{
			Currency = Currency.CreateStringValue(val, stringsBuffer, Size);
			Size += Currency.Length;
		}
	}
}