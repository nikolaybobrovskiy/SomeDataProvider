// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Linq;

	using Dtc.Protocol;
	using Dtc.Protocol.Private;

	using NUnit.Framework;

	using SomeDataProvider.DtcProtocolServer.Data;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol;

	// The series extension's messages in the server's codec, checked against Dtc.Protocol.Private's own decoding.
	[TestFixture]
	public class SeriesMessageCodecTests
	{
		[Test]
		public void MetadataResponse_RoundTripsWithUtf8Strings()
		{
			var response = new SeriesMetadataResponse
			{
				RequestID = 7,
				Symbol = "fred-GDPC1.pch",
				Title = "Real GDP: € and ü in UTF-8",
				Units = "Percent Change",
				UnitsShort = "% Chg.",
				Frequency = SeriesFrequency.Weekly,
				WeekEndDay = DayOfWeek.Friday,
				UnitsTransformation = SeriesUnitsTransformation.PercentChange,
				SeasonalAdjustment = SeriesSeasonalAdjustment.SeasonallyAdjustedAnnualRate,
				ObservationStart = new DateOnly(1947, 1, 1),
				ObservationEnd = new DateOnly(2026, 4, 1),
				LastUpdated = DtcTimestamp.FromDateTimeOffset(new DateTimeOffset(2026, 9, 25, 12, 51, 2, TimeSpan.Zero)),
			};
			var encoder = NewEncoder(EncodingEnum.BINARY_WITH_VARIABLE_LENGTH_STRINGS);

			encoder.EncodeSeriesMetadataResponse(response);

			Assert.That(SeriesWire.DecodeMetadataResponse(encoder.GetEncodedMessage()), Is.EqualTo(response));
		}

		[Test]
		public void VintageRecords_EncodeTheRangeAsOneMessageEach()
		{
			var rows = new[]
			{
				new SeriesVintage(new DateOnly(2025, 10, 1), new DateOnly(2026, 1, 29), new DateOnly(2026, 2, 26), 30_100.5),
				new SeriesVintage(new DateOnly(2025, 10, 1), new DateOnly(2026, 2, 27), new DateOnly(9999, 12, 31), null),
				new SeriesVintage(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30), new DateOnly(9999, 12, 31), -0.25),
			};
			var encoder = NewEncoder(EncodingEnum.BINARY_WITH_VARIABLE_LENGTH_STRINGS);

			encoder.EncodeSeriesVintageRecords(9, rows, 1, 2);
			encoder.EncodeSeriesVintagesTerminator(9);

			var frames = DtcWire.SplitFrames(encoder.GetEncodedMessage());
			var answer = SeriesWire.DecodeVintagesAnswer(frames, 9);
			Assert.That(answer.Reject, Is.Null);
			Assert.That(
				answer.Rows.Select(r => new SeriesVintage(r.ObservationDate!.Value, r.RealtimeStart!.Value, r.RealtimeEnd!.Value, r.Value)),
				Is.EqualTo(rows[1..]));
		}

		[Test]
		public void Reject_RoundTrips()
		{
			var reject = new SeriesRequestReject
			{
				RequestID = 11,
				RejectReason = SeriesRequestRejectReason.SourceUnavailable,
				RetryAfter = TimeSpan.FromSeconds(60),
				RejectText = "FRED's request rate limit is exceeded.",
			};
			var encoder = NewEncoder(EncodingEnum.BINARY_WITH_VARIABLE_LENGTH_STRINGS);

			encoder.EncodeSeriesRequestReject(reject);

			Assert.That(SeriesWire.DecodeReject(encoder.GetEncodedMessage()), Is.EqualTo(reject));
		}

		// A message larger than DTC's 65,535 bytes is refused before anything is sent, so the session can reject instead.
		[Test]
		public void MetadataResponse_TooLargeForOneMessage_ThrowsArgumentException()
		{
			var encoder = NewEncoder(EncodingEnum.BINARY_WITH_VARIABLE_LENGTH_STRINGS);

			Assert.That(
				() => encoder.EncodeSeriesMetadataResponse(new SeriesMetadataResponse { RequestID = 1, Symbol = "fred-X", Title = new string('T', ushort.MaxValue) }),
				Throws.InstanceOf<ArgumentException>());
		}

		// The extension exists only in binary VLS.
		[Test]
		public void BinaryEncoding_DoesNotSupportTheExtension()
		{
			var encoder = NewEncoder(EncodingEnum.BINARY_ENCODING);
			var decoder = MessageProtocol.CreateMessageProtocol(EncodingEnum.BINARY_ENCODING).MessageDecoderFactory.CreateMessageDecoder(new byte[64]);

			Assert.That(() => encoder.EncodeSeriesExtensionResponse(new SeriesExtensionResponse()), Throws.TypeOf<NotSupportedException>());
			Assert.That(() => encoder.EncodeSeriesMetadataResponse(new SeriesMetadataResponse()), Throws.TypeOf<NotSupportedException>());
			Assert.That(() => encoder.EncodeSeriesVintageRecords(1, Array.Empty<SeriesVintage>(), 0, 0), Throws.TypeOf<NotSupportedException>());
			Assert.That(() => encoder.EncodeSeriesVintagesTerminator(1), Throws.TypeOf<NotSupportedException>());
			Assert.That(() => encoder.EncodeSeriesRequestReject(new SeriesRequestReject()), Throws.TypeOf<NotSupportedException>());
			Assert.That(() => decoder.DecodeSeriesExtensionRequest(), Throws.TypeOf<NotSupportedException>());
			Assert.That(() => decoder.DecodeSeriesMetadataRequest(), Throws.TypeOf<NotSupportedException>());
			Assert.That(() => decoder.DecodeSeriesVintagesRequest(), Throws.TypeOf<NotSupportedException>());
		}

		[TestCase(DtcPrivateMessages.SeriesExtensionRequestType, "SERIES_EXTENSION_REQUEST")]
		[TestCase(DtcPrivateMessages.SeriesMetadataRequestType, "SERIES_METADATA_REQUEST")]
		[TestCase(DtcPrivateMessages.SeriesVintagesRequestType, "SERIES_VINTAGES_REQUEST")]
		[TestCase(DtcPrivateMessages.SeriesRequestRejectType, "SERIES_REQUEST_REJECT")]
		public void MessageTypeNames_NamePrivateTypesByTheRegistry(ushort type, string expected)
		{
			Assert.That(MessageTypeNames.Get(type), Is.EqualTo(expected));
		}

		static IMessageEncoder NewEncoder(EncodingEnum encoding) =>
			MessageProtocol.CreateMessageProtocol(encoding).MessageEncoderFactory.CreateMessageEncoder();
	}
}
