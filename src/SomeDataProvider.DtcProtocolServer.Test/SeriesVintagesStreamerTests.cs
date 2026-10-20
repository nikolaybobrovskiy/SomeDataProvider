// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Threading;
	using System.Threading.Tasks;

	using Dtc.Protocol.Private;

	using Microsoft.Extensions.Logging.Abstractions;

	using NUnit.Framework;

	using SomeDataProvider.DtcProtocolServer.Data;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol;

	// The answer to SERIES_VINTAGES_REQUEST (docs/dtc-series-extension.md §5): the rows and exactly one terminator, or
	// exactly one reject, at the end, with the rows checked against the extension's rules before they go out.
	[TestFixture]
	public class SeriesVintagesStreamerTests
	{
		const int RequestId = 61;

		static readonly DateOnly Latest = new(9999, 12, 31);

		// Second batches that break the rules after the row of the first batch: 2025-10-01, current 2026-01-29 to 2026-02-26.
		static readonly SeriesVintage[] InvalidSecondRows =
		{
			// An earlier observation date.
			new(new DateOnly(2025, 7, 1), new DateOnly(2026, 3, 1), Latest, 1),
			// The same observation, overlapping the previous real-time period.
			new(new DateOnly(2025, 10, 1), new DateOnly(2026, 2, 26), Latest, 1),
			// The same observation, with an earlier real-time start.
			new(new DateOnly(2025, 10, 1), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 28), 1),
			// A real-time start after its end.
			new(new DateOnly(2026, 1, 1), new DateOnly(2026, 5, 2), new DateOnly(2026, 5, 1), 1),
			// Values the extension cannot send.
			new(new DateOnly(2026, 1, 1), new DateOnly(2026, 5, 1), Latest, double.MaxValue),
			new(new DateOnly(2026, 1, 1), new DateOnly(2026, 5, 1), Latest, double.NaN),
		};

		static IMessageEncoderFactory EncoderFactory => MessageProtocol.CreateMessageProtocol(MessageProtocol.PreferredEncoding).MessageEncoderFactory;

		[TestCase(0)]
		[TestCase(1)]
		[TestCase(SeriesVintagesStreamer.RowsPerSend)]
		[TestCase(SeriesVintagesStreamer.RowsPerSend + 1)]
		public async Task Stream_SendsTheRowsInSendsOfAtMostRowsPerSend_AndThenOneTerminator(int rowsCount)
		{
			var rows = Rows(rowsCount);

			var (answer, sends) = await StreamAsync(Batches(rows));

			Assert.That(answer.Reject, Is.Null);
			Assert.That(answer.Rows.Select(ToVintage), Is.EqualTo(rows));
			var expectedSends = Enumerable.Range(0, (rowsCount + SeriesVintagesStreamer.RowsPerSend - 1) / SeriesVintagesStreamer.RowsPerSend)
				.Select(i => Math.Min(SeriesVintagesStreamer.RowsPerSend, rowsCount - (i * SeriesVintagesStreamer.RowsPerSend)))
				.Append(1);
			Assert.That(sends.Select(s => s.Count), Is.EqualTo(expectedSends));
		}

		// A batch goes out before the next batch is read, so the client hears from the server while the source pages.
		[Test]
		public async Task Stream_SendsEachBatchBeforeReadingTheNext()
		{
			var rows = Rows(5);
			var sendsBeforeBatch = new List<int>();
			var sends = new List<IReadOnlyList<byte[]>>();

			await SeriesVintagesStreamer.StreamAsync(
				Batches(rows[..2], rows[2..]).Observe(() => sendsBeforeBatch.Add(sends.Count)),
				RequestId,
				EncoderFactory,
				bytes =>
				{
					sends.Add(DtcWire.SplitFrames(bytes));
					return true;
				},
				NullLogger.Instance,
				CancellationToken.None);

			Assert.That(sendsBeforeBatch, Is.EqualTo(new[] { 0, 1, 2 }), "Sends made before each batch was read, and before the end was found.");
		}

		[Test]
		public async Task Stream_OfRowsAcrossBatchesOfOneObservation_SendsThemAll()
		{
			var date = new DateOnly(2025, 10, 1);
			var first = new SeriesVintage(date, new DateOnly(2026, 1, 29), new DateOnly(2026, 2, 26), 1);
			var second = new SeriesVintage(date, new DateOnly(2026, 2, 27), Latest, null);

			var (answer, _) = await StreamAsync(Batches(new[] { first }, new[] { second }));

			Assert.That(answer.Reject, Is.Null);
			Assert.That(answer.Rows.Select(ToVintage), Is.EqualTo(new[] { first, second }));
		}

		// Rows that break the extension's rules would fail the request at the client; the server rejects instead.
		[Test]
		public async Task Stream_OfABatchThatBreaksTheRules_SendsTheEarlierRowsAndThenOneReject([Range(0, 5)] int invalidRow)
		{
			var valid = new SeriesVintage(new DateOnly(2025, 10, 1), new DateOnly(2026, 1, 29), new DateOnly(2026, 2, 26), 1);

			var (answer, _) = await StreamAsync(Batches(new[] { valid }, new[] { InvalidSecondRows[invalidRow] }));

			Assert.That(answer.Rows.Select(ToVintage), Is.EqualTo(new[] { valid }));
			Assert.That(answer.Reject?.RejectReason, Is.EqualTo(SeriesRequestRejectReason.SourceUnavailable));
			Assert.That(answer.Reject?.RejectText, Does.StartWith("The source returned rows the extension cannot send"));
		}

		[Test]
		public async Task Stream_WhenTheSourceRefuses_SendsOneRejectWithItsReasonAndHint()
		{
			var refusal = new SeriesSourceException(SeriesRequestRejectReason.SourceUnavailable, "FRED's request rate limit is exceeded.", TimeSpan.FromSeconds(60));

			var (answer, sends) = await StreamAsync(Failing(refusal, Rows(3)));

			Assert.That(answer.Rows, Has.Count.EqualTo(3));
			Assert.That(answer.Reject, Is.EqualTo(new SeriesRequestReject
			{
				RequestID = RequestId,
				RejectReason = SeriesRequestRejectReason.SourceUnavailable,
				RetryAfter = TimeSpan.FromSeconds(60),
				RejectText = "FRED's request rate limit is exceeded.",
			}));
			Assert.That(sends, Has.Count.EqualTo(2));
		}

		[Test]
		public async Task Stream_WhenTheSourceFailsUnexpectedly_SendsOneSourceUnavailableReject()
		{
			var (answer, _) = await StreamAsync(Failing(new InvalidOperationException("A defect."), Array.Empty<SeriesVintage>()));

			Assert.That(answer.Rows, Is.Empty);
			Assert.That(answer.Reject?.RejectReason, Is.EqualTo(SeriesRequestRejectReason.SourceUnavailable));
			Assert.That(answer.Reject?.RetryAfter, Is.Null);
			Assert.That(answer.Reject?.RejectText, Does.Not.Contain("A defect"), "The reject quotes an internal exception.");
		}

		// A closing connection accepts no more sends, so the answer stops without a terminator.
		[Test]
		public async Task Stream_WhenASendFails_StopsWithoutTheTerminator()
		{
			var sends = 0;

			await SeriesVintagesStreamer.StreamAsync(Batches(Rows(3), Rows(2, 10)), RequestId, EncoderFactory, _ => ++sends < 1, NullLogger.Instance, CancellationToken.None);

			Assert.That(sends, Is.EqualTo(1));
		}

		[Test]
		public void Stream_WhenCanceled_SendsNothingMore()
		{
			using var cancellation = new CancellationTokenSource();
			var sends = 0;

			Assert.That(
				async () => await SeriesVintagesStreamer.StreamAsync(
					Batches(Rows(3), Rows(2, 10)).Observe(cancellation.Cancel, skip: 1),
					RequestId,
					EncoderFactory,
					_ =>
					{
						sends++;
						return true;
					},
					NullLogger.Instance,
					cancellation.Token),
				Throws.InstanceOf<OperationCanceledException>());
			Assert.That(sends, Is.EqualTo(1), "Only the first batch went out.");
		}

		static SeriesVintage[] Rows(int count, int firstDay = 0)
		{
			var start = new DateOnly(1990, 1, 1);
			return Enumerable.Range(firstDay, count)
				.Select(i => new SeriesVintage(start.AddDays(i), start.AddDays(i + 30), Latest, i))
				.ToArray();
		}

		static async IAsyncEnumerable<IReadOnlyList<SeriesVintage>> Batches(params IReadOnlyList<SeriesVintage>[] batches)
		{
			foreach (var batch in batches)
			{
				await Task.Yield();
				yield return batch;
			}
		}

		static async IAsyncEnumerable<IReadOnlyList<SeriesVintage>> Failing(Exception failure, IReadOnlyList<SeriesVintage> before)
		{
			await Task.Yield();
			yield return before;
			throw failure;
		}

		static SeriesVintage ToVintage(SeriesVintageRecord row) =>
			new(row.ObservationDate!.Value, row.RealtimeStart!.Value, row.RealtimeEnd!.Value, row.Value);

		static async Task<(VintagesAnswer Answer, List<IReadOnlyList<byte[]>> Sends)> StreamAsync(IAsyncEnumerable<IReadOnlyList<SeriesVintage>> batches)
		{
			var sends = new List<IReadOnlyList<byte[]>>();
			await SeriesVintagesStreamer.StreamAsync(
				batches,
				RequestId,
				EncoderFactory,
				bytes =>
				{
					sends.Add(DtcWire.SplitFrames(bytes));
					return true;
				},
				NullLogger.Instance,
				CancellationToken.None);
			return (SeriesWire.DecodeVintagesAnswer(sends.SelectMany(s => s).ToList(), RequestId), sends);
		}
	}

	static class AsyncEnumerableTestExtensions
	{
		/// <summary>Calls <paramref name="beforeEachRead"/> before each read of the next element, after skipping the first <paramref name="skip"/> reads.</summary>
		public static async IAsyncEnumerable<T> Observe<T>(this IAsyncEnumerable<T> source, Action beforeEachRead, int skip = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
			var reads = 0;
			while (true)
			{
				if (reads++ >= skip)
					beforeEachRead();
				cancellationToken.ThrowIfCancellationRequested();
				if (!await enumerator.MoveNextAsync())
					yield break;
				yield return enumerator.Current;
			}
		}
	}
}
