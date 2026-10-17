// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;

	using Dtc.Protocol;

	using Microsoft.Extensions.Logging.Abstractions;

	using NUnit.Framework;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol;

	// PLAN.md §7.3 items 2 and 3: one header per response, and a response with records ends with exactly one final record.
	[TestFixture]
	public class HistoricalPriceDataStreamerTests
	{
		const int RequestId = 42;

		[TestCase(0)]
		[TestCase(1)]
		[TestCase(921)]
		[TestCase(922)]
		[TestCase(923)]
		[TestCase(1844)]
		[TestCase(1845)]
		public async Task Stream_HasOneHeaderAndEndsWithOneFinalRecord(int observationsCount)
		{
			var history = new FakeHistory(observationsCount);

			var (response, sends) = await StreamAsync(history);

			Assert.That(response.Header.RequestId, Is.EqualTo(RequestId));
			Assert.That(response.Header.RecordInterval, Is.EqualTo(DtcWire.IntervalOneDay));
			Assert.That(response.Header.UseZLibCompression, Is.EqualTo(0));
			Assert.That(response.Header.NoRecordsToReturn, Is.EqualTo(observationsCount == 0 ? 1 : 0));
			AssertRecordsMatch(response, history);
			// The header goes out alone, then one send per batch of 922 records.
			Assert.That(sends.Select(s => s.Count), Is.EqualTo(new[] { 1 }.Concat(Batches(observationsCount)).ToArray()));
		}

		[Test]
		public async Task Stream_OfZeroRecords_SetsNoRecordsToReturnAndSendsNoRecord()
		{
			// DTC: when NoRecordsToReturn is set, the server sends no further record for the request, final or not.
			var (response, sends) = await StreamAsync(new FakeHistory(0));

			Assert.That(response.Header.NoRecordsToReturn, Is.EqualTo(1));
			Assert.That(response.Records, Is.Empty);
			Assert.That(sends, Has.Count.EqualTo(1));
		}

		[Test]
		public async Task Stream_WithAPageOfMissingValuesInTheMiddle_SendsEveryRecordAndOneFinalRecord()
		{
			// Observations 922–1843 are all missing ("."), so the second page has no records but more pages follow.
			var history = new FakeHistory(2000, i => i is >= 922 and < 1844);

			var (response, sends) = await StreamAsync(history);

			Assert.That(response.Header.NoRecordsToReturn, Is.EqualTo(0));
			AssertRecordsMatch(response, history);
			Assert.That(response.Records, Has.Count.EqualTo(922 + 156));
			Assert.That(sends.Select(s => s.Count), Is.EqualTo(new[] { 1, 922, 156 }));
		}

		[Test]
		public async Task Stream_EndingWithPagesOfMissingValues_MarksTheLastRealRecordFinal()
		{
			// The pages after the first hold only missing values, and the last of them is full, so one more empty page follows.
			var history = new FakeHistory(2766, i => i >= 922);

			var (response, _) = await StreamAsync(history);

			AssertRecordsMatch(response, history);
			Assert.That(response.Records, Has.Count.EqualTo(922));
			Assert.That(response.Records[^1].IsFinalRecord, Is.EqualTo(1));
			Assert.That(history.ReadCount, Is.EqualTo(4));
		}

		[Test]
		public async Task Stream_StartingWithAPageOfMissingValues_StillSendsTheLaterRecords()
		{
			// The old code set NoRecordsToReturn whenever the first page was empty, even when later pages had records.
			var history = new FakeHistory(1000, i => i < 922);

			var (response, _) = await StreamAsync(history);

			Assert.That(response.Header.NoRecordsToReturn, Is.EqualTo(0));
			AssertRecordsMatch(response, history);
			Assert.That(response.Records, Has.Count.EqualTo(78));
		}

		[TestCase(5)]
		[TestCase(923)]
		[TestCase(1844)]
		public async Task Stream_OfOnlyMissingValues_SetsNoRecordsToReturn(int observationsCount)
		{
			var history = new FakeHistory(observationsCount, _ => true);

			var (response, _) = await StreamAsync(history);

			Assert.That(response.Header.NoRecordsToReturn, Is.EqualTo(1));
			Assert.That(response.Records, Is.Empty);
		}

		static IEnumerable<int> Batches(int recordsCount)
		{
			for (var remaining = recordsCount; remaining > 0; remaining -= FakeHistory.BatchSize)
				yield return Math.Min(remaining, FakeHistory.BatchSize);
		}

		static void AssertRecordsMatch(HistoryResponse response, FakeHistory history)
		{
			var expectedIndexes = history.RecordIndexes.ToArray();
			Assert.That(response.Records, Has.Count.EqualTo(expectedIndexes.Length));
			Assert.That(response.Records.Select(r => r.RequestId), Is.All.EqualTo(RequestId));
			Assert.That(
				response.Records.Select(r => r.StartDateTime),
				Is.EqualTo(expectedIndexes.Select(i => DtcDateTime.ToUnixMicroseconds(FakeHistory.DateOf(i)))));
			Assert.That(response.Records.Select(r => r.LastPrice), Is.EqualTo(expectedIndexes.Select(FakeHistory.ValueOf)));
		}

		static async Task<(HistoryResponse Response, List<List<byte[]>> Sends)> StreamAsync(FakeHistory history)
		{
			var protocol = MessageProtocol.CreateMessageProtocol(MessageProtocol.PreferredEncoding);
			var sends = new List<List<byte[]>>();
			using var reader = history.CreateReader();
			await HistoricalPriceDataStreamer.StreamAsync(
				reader,
				RequestId,
				HistoricalDataIntervalEnum.INTERVAL_1_DAY,
				protocol.MessageEncoderFactory,
				bytes => sends.Add(DtcWire.SplitFrames(bytes)),
				NullLogger.Instance,
				CancellationToken.None);
			return (DtcWire.DecodeHistoryResponse(sends.SelectMany(s => s).ToList()), sends);
		}
	}
}
