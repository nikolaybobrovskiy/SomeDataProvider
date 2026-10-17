// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.IO;
	using System.Runtime.CompilerServices;
	using System.Text;

	using Dtc.Protocol;
	using Dtc.Protocol.Vls;

	using NUnit.Framework;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol;

	using Records = Dtc.Protocol.Messages;
	using VlsHistoricalPriceDataReject = Dtc.Protocol.Vls.s_HistoricalPriceDataReject;
	using VlsLogonResponse = Dtc.Protocol.Vls.s_LogonResponse;
	using VlsMarketDataReject = Dtc.Protocol.Vls.s_MarketDataReject;
	using VlsSecurityDefinitionForSymbolRequest = Dtc.Protocol.Vls.s_SecurityDefinitionForSymbolRequest;
	using VlsSecurityDefinitionReject = Dtc.Protocol.Vls.s_SecurityDefinitionReject;
	using VlsSecurityDefinitionResponse = Dtc.Protocol.Vls.s_SecurityDefinitionResponse;

	/// <summary>
	/// The server's messages as a DTC client decodes them with Dtc.Protocol, and the server's decoding of requests that
	/// Dtc.Protocol encodes. The expected records list every field, so a header default that the server does not override
	/// shows up as a difference.
	/// </summary>
	[TestFixture]
	public class MessageCodecTests
	{
		static readonly Encoding Ascii = Encoding.ASCII;

		[TestCase(false)]
		[TestCase(true)]
		public void LogonResponse_AdvertisesOnlyWhatTheServerSupports(bool historyOnly)
		{
			var encoder = NewEncoder();
			encoder.EncodeLogonResponse(LogonStatusEnum.LOGON_SUCCESS, "Logon is successful.", historyOnly);

			var frame = encoder.GetEncodedMessage();
			DtcVlsDecoder.Decode(frame, out VlsLogonResponse message);
			Assert.That(message.BaseSize, Is.EqualTo(Unsafe.SizeOf<VlsLogonResponse>()));
			Assert.That(Records.LogonResponse.FromVls(in message, frame, Ascii), Is.EqualTo(new Records.LogonResponse
			{
				Result = LogonStatusEnum.LOGON_SUCCESS,
				ResultText = "Logon is successful.",
				OrderCancelReplaceSupported = false,
				SecurityDefinitionsSupported = true,
				HistoricalPriceDataSupported = true,
				ResubscribeWhenMarketDataFeedAvailable = true,
				MarketDepthIsSupported = false,
				OneHistoricalPriceDataRequestPerConnection = historyOnly,
				MarketDataSupported = true,
			}));
		}

		[Test]
		public void SecurityDefinitionResponse_ForAFredSeries()
		{
			var encoder = NewEncoder();
			encoder.EncodeSecurityDefinitionResponse(new IMessageEncoder.EncodeSecurityDefinitionResponseArgs(17, new FakeSymbol(FakeStores.SeriesId), true)
			{
				Symbol = "fred-" + FakeStores.SeriesId,
			});

			Assert.That(DecodeSecurityDefinitionResponse(encoder.GetEncodedMessage()), Is.EqualTo(new Records.SecurityDefinitionResponse
			{
				RequestID = 17,
				Symbol = "fred-GDP",
				Description = "Fake series (Index/D/NSA)",
				MinPriceIncrement = 0.01F,
				PriceDisplayFormat = PriceDisplayFormatEnum.PRICE_DISPLAY_FORMAT_DECIMAL_2,
				IsFinalMessage = true,
				HasMarketDepthData = false,
				IsDelayed = true,
			}));
		}

		[Test]
		public void SecurityDefinitionResponse_WithExchangeAndCurrency()
		{
			var encoder = NewEncoder();
			encoder.EncodeSecurityDefinitionResponse(new IMessageEncoder.EncodeSecurityDefinitionResponseArgs(18, new StockSymbol(), false));

			Assert.That(DecodeSecurityDefinitionResponse(encoder.GetEncodedMessage()), Is.EqualTo(new Records.SecurityDefinitionResponse
			{
				RequestID = 18,
				Symbol = "AAPL",
				Exchange = "NASDAQ",
				// The server has never sent the symbol's type.
				SecurityType = SecurityTypeEnum.SECURITY_TYPE_UNSET,
				Description = "Apple Inc.",
				MinPriceIncrement = 0.0001F,
				PriceDisplayFormat = PriceDisplayFormatEnum.PRICE_DISPLAY_FORMAT_DECIMAL_4,
				HasMarketDepthData = false,
				Currency = "USD",
			}));
		}

		[Test]
		public void NoSecurityDefinitionsFound_IsAnEmptyFinalResponse()
		{
			var encoder = NewEncoder();
			encoder.EncodeNoSecurityDefinitionsFound(19);

			Assert.That(DecodeSecurityDefinitionResponse(encoder.GetEncodedMessage()), Is.EqualTo(new Records.SecurityDefinitionResponse
			{
				RequestID = 19,
				IsFinalMessage = true,
				HasMarketDepthData = false,
			}));
		}

		[Test]
		public void Encoder_AppendsEachMessageAfterTheOnesBefore()
		{
			var encoder = NewEncoder();
			encoder.EncodeSecurityDefinitionResponse(new IMessageEncoder.EncodeSecurityDefinitionResponseArgs(0, new FakeSymbol(FakeStores.SeriesId), false));
			encoder.EncodeSecurityDefinitionResponse(new IMessageEncoder.EncodeSecurityDefinitionResponseArgs(0, new StockSymbol(), true));

			var frames = DtcWire.SplitFrames(encoder.GetEncodedMessage());
			Assert.That(frames, Has.Count.EqualTo(2));
			Assert.That(encoder.EncodedMessageSize, Is.EqualTo(frames[0].Length + frames[1].Length));
			Assert.That(DecodeSecurityDefinitionResponse(frames[0]).Symbol, Is.EqualTo(FakeStores.SeriesId));
			Assert.That(DecodeSecurityDefinitionResponse(frames[0]).IsFinalMessage, Is.False);
			Assert.That(DecodeSecurityDefinitionResponse(frames[1]).Symbol, Is.EqualTo("AAPL"));
			Assert.That(DecodeSecurityDefinitionResponse(frames[1]).IsFinalMessage, Is.True);
		}

		[Test]
		public void Encoder_WithNoMessage_Throws()
		{
			Assert.That(() => NewEncoder().GetEncodedMessage(), Throws.InvalidOperationException);
		}

		// Text goes out as ASCII, as it always has: any other character becomes '?'.
		[TestCase("Symbol is unknown: fred-X.", "Symbol is unknown: fred-X.")]
		[TestCase("Symbol is unknown: \u00c4\u20ac.", "Symbol is unknown: ??.")]
		public void HistoricalPriceDataReject_CarriesTheReasonAndText(string text, string expectedText)
		{
			var encoder = NewEncoder();
			encoder.EncodeHistoricalPriceDataReject(7, HistoricalPriceDataRejectReasonCodeEnum.HPDR_GENERAL_REJECT_ERROR, text);

			var frame = encoder.GetEncodedMessage();
			DtcVlsDecoder.Decode(frame, out VlsHistoricalPriceDataReject message);
			Assert.That(Records.HistoricalPriceDataReject.FromVls(in message, frame, Ascii), Is.EqualTo(new Records.HistoricalPriceDataReject
			{
				RequestID = 7,
				RejectText = expectedText,
				RejectReasonCode = HistoricalPriceDataRejectReasonCodeEnum.HPDR_GENERAL_REJECT_ERROR,
			}));
		}

		[TestCase("Real-time market data is not supported for fred-GDP.")]
		[TestCase("")]
		public void MarketDataReject_CarriesTheSymbolIdAndText(string text)
		{
			var encoder = NewEncoder();
			encoder.EncodeMarketDataReject(3, text);

			var frame = encoder.GetEncodedMessage();
			DtcVlsDecoder.Decode(frame, out VlsMarketDataReject message);
			Assert.That(Records.MarketDataReject.FromVls(in message, frame, Ascii), Is.EqualTo(new Records.MarketDataReject { SymbolID = 3, RejectText = text }));
			if (text.Length == 0)
				Assert.That(frame, Has.Length.EqualTo(Unsafe.SizeOf<VlsMarketDataReject>()), "An empty text is absent.");
		}

		[Test]
		public void SecurityDefinitionReject_CarriesTheRequestIdAndText()
		{
			var encoder = NewEncoder();
			encoder.EncodeSecurityDefinitionReject(9, "Symbol is unknown: fred-X.");

			var frame = encoder.GetEncodedMessage();
			DtcVlsDecoder.Decode(frame, out VlsSecurityDefinitionReject message);
			Assert.That(Records.SecurityDefinitionReject.FromVls(in message, frame, Ascii), Is.EqualTo(new Records.SecurityDefinitionReject
			{
				RequestID = 9,
				RejectText = "Symbol is unknown: fred-X.",
			}));
		}

		[Test]
		public void EncodingResponse_NamesTheEncodingAndProtocolVersion8()
		{
			var encoder = NewEncoder();
			encoder.EncodeEncodingResponse(EncodingEnum.BINARY_WITH_VARIABLE_LENGTH_STRINGS);

			DtcBinaryDecoder.Decode(encoder.GetEncodedMessage(), out s_EncodingResponse message);
			Assert.That(Records.EncodingResponse.FromBinary(in message, Ascii), Is.EqualTo(new Records.EncodingResponse
			{
				ProtocolVersion = 8,
				Encoding = EncodingEnum.BINARY_WITH_VARIABLE_LENGTH_STRINGS,
				ProtocolType = "DTC",
			}));
		}

		[Test]
		public void Heartbeat_HasNoTime()
		{
			var encoder = NewEncoder();
			encoder.EncodeHeartbeatMessage(0);

			DtcBinaryDecoder.Decode(encoder.GetEncodedMessage(), out s_Heartbeat message);
			Assert.That(Records.Heartbeat.FromBinary(in message), Is.EqualTo(new Records.Heartbeat { NumDroppedMessages = 0, CurrentDateTime = null }));
		}

		[Test]
		public void HistoryHeader_HasTheCurrentHeadersLayout()
		{
			var encoder = NewEncoder();
			encoder.EncodeHistoricalPriceDataResponseHeader(23, HistoricalDataIntervalEnum.INTERVAL_1_DAY, false, true, 1);

			var frame = encoder.GetEncodedMessage();
			Assert.That(frame, Has.Length.EqualTo(24), "The current header added UseZLibNGCompression.");
			DtcBinaryDecoder.Decode(frame, out s_HistoricalPriceDataResponseHeader message);
			Assert.That(Records.HistoricalPriceDataResponseHeader.FromBinary(in message), Is.EqualTo(new Records.HistoricalPriceDataResponseHeader
			{
				RequestID = 23,
				RecordInterval = HistoricalDataIntervalEnum.INTERVAL_1_DAY,
				NoRecordsToReturn = true,
				IntToFloatPriceDivisor = 1,
			}));
		}

		[Test]
		public void MarketDataSnapshot_SetsOnlyTheTradingStatusAndLastTradeTime()
		{
			var encoder = NewEncoder();
			encoder.EncodeMarketDataSnapshot(3, TradingStatusEnum.TRADING_STATUS_UNKNOWN, new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc));

			DtcBinaryDecoder.Decode(encoder.GetEncodedMessage(), out s_MarketDataSnapshot message);
			Assert.That(message.SymbolID, Is.EqualTo(3));
			Assert.That(message.TradingStatus, Is.EqualTo(TradingStatusEnum.TRADING_STATUS_UNKNOWN));
			Assert.That(message.LastTradeDateTime, Is.EqualTo(1_709_294_400.0));
			// Everything else keeps the header's unset values.
			var unset = new s_MarketDataSnapshot { SymbolID = 3, LastTradeDateTime = 1_709_294_400.0 };
			Assert.That(DtcWire.SplitFrames(encoder.GetEncodedMessage())[0], Is.EqualTo(Encode(unset)));
		}

		[Test]
		public void LogonRequest_IsDecoded()
		{
			var frame = ToVls(new Records.LogonRequest
			{
				Username = "user",
				Password = "secret",
				HeartbeatIntervalInSeconds = 10,
				HardwareIdentifier = "HW-1234",
				ClientName = "SierraChart",
			});

			var request = NewDecoder(frame).DecodeLogonRequest();

			Assert.That(request.HeartbeatIntervalInSeconds, Is.EqualTo(10));
			Assert.That(request.ClientName, Is.EqualTo("SierraChart"));
			Assert.That(request.HardwareIdentifier, Is.EqualTo("HW-1234"));
		}

		[Test]
		public void MarketDataRequest_IsDecoded()
		{
			var frame = ToVls(new Records.MarketDataRequest
			{
				RequestAction = RequestActionEnum.SUBSCRIBE,
				SymbolID = 3,
				Symbol = "fred-GDP",
				IntervalForSnapshotUpdatesInMilliseconds = 1000,
			});

			var decoder = NewDecoder(frame);
			Assert.That(decoder.DecodeMessageType(), Is.EqualTo(DtcConstants.MARKET_DATA_REQUEST));
			var request = decoder.DecodeMarketDataRequest();

			Assert.That(request.RequestAction, Is.EqualTo(RequestActionEnum.SUBSCRIBE));
			Assert.That(request.SymbolId, Is.EqualTo(3));
			Assert.That(request.Symbol, Is.EqualTo("fred-GDP"));
			Assert.That(request.Exchange, Is.Empty);
			Assert.That(request.IntervalForSnapshotUpdatesInMilliseconds, Is.EqualTo(1000));
		}

		[Test]
		public void SecurityDefinitionForSymbolRequest_IsDecoded()
		{
			var frame = ToVls(new Records.SecurityDefinitionForSymbolRequest { RequestID = 18, Symbol = "fred-T5YIFR", Exchange = "FRED" });

			var request = NewDecoder(frame).DecodeSecurityDefinitionForSymbolRequest();

			Assert.That(request.ToString(), Is.EqualTo("18/fred-T5YIFR/FRED"));
		}

		[Test]
		public void Request_WithAMalformedString_IsRejected()
		{
			var frame = ToVls(new Records.SecurityDefinitionForSymbolRequest { RequestID = 18, Symbol = "fred-GDP" });
			// Overwrite the symbol's null terminator, the last byte of the message.
			frame[^1] = (byte)'X';

			Assert.That(() => NewDecoder(frame).DecodeSecurityDefinitionForSymbolRequest(), Throws.TypeOf<InvalidDataException>());
		}

		// The server has always decoded at most 4096 bytes of a string, so a reply that quotes it fits in a message.
		[TestCase(4096, 4096)]
		[TestCase(4097, 4096)]
		[TestCase(-1, 4096)]
		public void Request_WithALongString_KeepsItsFirst4096Bytes(int symbolLength, int expectedLength)
		{
			if (symbolLength < 0)
			{
				// The longest symbol a message can hold: all bytes after the fixed part, less the null terminator.
				symbolLength = ushort.MaxValue - Unsafe.SizeOf<VlsSecurityDefinitionForSymbolRequest>() - 1;
			}
			var symbol = new string('S', symbolLength - 1) + "E";
			var frame = ToVls(new Records.SecurityDefinitionForSymbolRequest { RequestID = 18, Symbol = symbol });

			var request = NewDecoder(frame).DecodeSecurityDefinitionForSymbolRequest();
			Assert.That(request.Symbol, Is.EqualTo(symbol[..expectedLength]));

			var encoder = NewEncoder();
			encoder.EncodeSecurityDefinitionReject(request.RequestId, $"Symbol is unknown: {request.Symbol}.");
			var reply = encoder.GetEncodedMessage();
			DtcVlsDecoder.Decode(reply, out VlsSecurityDefinitionReject message);
			Assert.That(Records.SecurityDefinitionReject.FromVls(in message, reply, Ascii).RejectText, Is.EqualTo($"Symbol is unknown: {request.Symbol}."));
		}

		[TestCase(DtcConstants.ENCODING_REQUEST, "EncodingRequest")]
		[TestCase(DtcConstants.HISTORICAL_PRICE_DATA_REQUEST, "HistoricalPriceDataRequest")]
		[TestCase(DtcConstants.SYMBOL_SEARCH_REQUEST, "SymbolSearchRequest")]
		[TestCase((ushort)12345, "12345")]
		public void MessageTypeNames_NameTheStructureOrGiveTheNumber(ushort type, string expected)
		{
			Assert.That(MessageTypeNames.Get(type), Is.EqualTo(expected));
		}

		static IMessageEncoder NewEncoder() =>
			MessageProtocol.CreateMessageProtocol(MessageProtocol.PreferredEncoding).MessageEncoderFactory.CreateMessageEncoder();

		static IMessageDecoder NewDecoder(byte[] frame) =>
			MessageProtocol.CreateMessageProtocol(MessageProtocol.PreferredEncoding).MessageDecoderFactory.CreateMessageDecoder(frame);

		static Records.SecurityDefinitionResponse DecodeSecurityDefinitionResponse(byte[] frame)
		{
			DtcVlsDecoder.Decode(frame, out VlsSecurityDefinitionResponse message);
			Assert.That(message.BaseSize, Is.EqualTo(Unsafe.SizeOf<VlsSecurityDefinitionResponse>()));
			return Records.SecurityDefinitionResponse.FromVls(in message, frame, Ascii);
		}

		static byte[] Encode<T>(in T message)
			where T : unmanaged, IDtcBinaryMessage
		{
			var frame = new byte[DtcBinaryEncoder.GetEncodedLength<T>()];
			DtcBinaryEncoder.Encode(in message, frame);
			return frame;
		}

		static byte[] ToVls(Records.LogonRequest record) => ToVls(buffer => record.ToVls(buffer, Ascii));

		static byte[] ToVls(Records.MarketDataRequest record) => ToVls(buffer => record.ToVls(buffer, Ascii));

		static byte[] ToVls(Records.SecurityDefinitionForSymbolRequest record) => ToVls(buffer => record.ToVls(buffer, Ascii));

		static byte[] ToVls(Func<byte[], int> encode)
		{
			var buffer = new byte[ushort.MaxValue];
			return buffer.AsSpan(0, encode(buffer)).ToArray();
		}

		sealed class StockSymbol : ISymbol
		{
			public string Code => "AAPL";

			public string? Exchange => "NASDAQ";

			public SymbolType Type => SymbolType.Stock;

			public string? Description => "Apple Inc.";

			public string? Category => null;

			public int NumberOfDecimals => 4;

			public float MinPriceIncrement => 0.0001F;

			public string? Currency => "USD";

			public bool IsRealTime => false;

			public bool IsDelayed => false;

			public bool IsDiscontinued => false;

			public DataService DataService => DataService.Fred;

			public string? DataServiceSettings => null;
		}
	}
}
