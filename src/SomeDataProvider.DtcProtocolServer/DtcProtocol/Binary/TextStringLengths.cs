// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	static class TextStringLengths
	{
		public const int UsernamePasswordLength = 32;
		public const int SymbolExchangeDelimiterLength = 4;
		public const int SymbolLength = 64;
		public const int ExchangeLength = 16;
		public const int UnderlyingSymbolLength = 32;
		public const int SymbolDescriptionLength = 64;
		public const int ExchangeDescriptionLength = 48;
		public const int OrderIdLength = 32;
		public const int TradeAccountLength = 32;
		public const int TextDescriptionLength = 96;
		public const int TextMessageLength = 256;
		public const int OrderFreeFormTextLength = 48;
		public const int ClientServerNameLength = 48;
		public const int GeneralIdentifierLength = 64;
		public const int CurrencyCodeLength = 8;
		public const int OrderFillExecutionLength = 64;
	}
}