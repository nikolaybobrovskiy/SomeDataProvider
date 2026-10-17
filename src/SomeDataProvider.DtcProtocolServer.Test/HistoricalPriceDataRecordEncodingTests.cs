// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Globalization;

	using NUnit.Framework;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	// PLAN.md §7.3 item 1: history records carry StartDateTime as t_DateTimeWithMicrosecondsInt.
	[TestFixture]
	public class HistoricalPriceDataRecordEncodingTests
	{
		[TestCase(true, "2024-03-01T00:00:00.0000000Z", 1_709_251_200_000_000L)]
		[TestCase(true, "2024-03-01T12:34:56.7890120Z", 1_709_296_496_789_012L)]
		[TestCase(true, "1970-01-01T00:00:00.0000000Z", 0L)]
		[TestCase(true, "1970-01-01T00:00:00.0000010Z", 1L)]
		[TestCase(true, "1969-12-31T23:59:59.9999990Z", -1L)]
		[TestCase(true, "1947-01-01T00:00:00.0000000Z", -725_846_400_000_000L)]
		[TestCase(false, "2024-03-01T00:00:00.0000000Z", 1_709_251_200_000_000L)]
		[TestCase(false, "1947-01-01T00:00:00.0000000Z", -725_846_400_000_000L)]
		public void Record_CarriesStartDateTimeInMicroseconds(bool variableLengthStrings, string startDateTime, long expectedMicroseconds)
		{
			var encoding = variableLengthStrings ? EncodingEnum.BinaryWithVariableLengthStrings : EncodingEnum.BinaryEncoding;
			var dateTime = DateTime.Parse(startDateTime, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
			Assert.That(dateTime.Kind, Is.EqualTo(DateTimeKind.Utc));
			var encoder = MessageProtocol.CreateMessageProtocol(encoding).MessageEncoderFactory.CreateMessageEncoder();

			encoder.EncodeHistoricalPriceDataRecordResponse(7, dateTime, 12.5, true);

			var record = DtcWire.DecodeHistoryRecord(encoder.GetEncodedMessage());
			Assert.That(record.StartDateTime, Is.EqualTo(expectedMicroseconds));
			Assert.That(record.RequestId, Is.EqualTo(7));
			Assert.That(record.LastPrice, Is.EqualTo(12.5));
			Assert.That(record.IsFinalRecord, Is.EqualTo(1));
		}

		[Test]
		public void ToUnixMicroseconds_RoundsSubMicrosecondTicksDown()
		{
			var epoch = DateTime.UnixEpoch;
			Assert.That(DtcDateTime.ToUnixMicroseconds(epoch.AddTicks(9)), Is.EqualTo(0));
			Assert.That(DtcDateTime.ToUnixMicroseconds(epoch.AddTicks(-1)), Is.EqualTo(-1));
			Assert.That(DtcDateTime.ToUnixMicroseconds(epoch.AddTicks(-10)), Is.EqualTo(-1));
			Assert.That(DtcDateTime.ToUnixMicroseconds(epoch.AddTicks(-11)), Is.EqualTo(-2));
		}

		[Test]
		public void ToUnixMicroseconds_ConvertsLocalTimeToUtc()
		{
			var utc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
			Assert.That(DtcDateTime.ToUnixMicroseconds(utc.ToLocalTime()), Is.EqualTo(1_709_251_200_000_000L));
		}
	}
}
