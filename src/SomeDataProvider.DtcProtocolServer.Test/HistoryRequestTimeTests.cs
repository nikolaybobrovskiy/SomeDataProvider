// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Net;
	using System.Net.Http;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Web;

	using Dtc.Protocol;

	using Microsoft.Extensions.Logging.Abstractions;

	using NUnit.Framework;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DataStorage.Fred;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol;

	/// <summary>
	/// PLAN.md §7.3 item 5: request times pass through NBLib's <c>UnixTimeStampToDateTimeUtc</c>. 0 must mean the earliest
	/// or latest available observation, and negative times must reach FRED as dates before 1970.
	/// </summary>
	/// <remarks>
	/// FRED is replaced by a message handler that records the request and answers with a fixture in the documented
	/// <c>fred/series/observations</c> JSON format. No test calls FRED.
	/// </remarks>
	[TestFixture]
	public class HistoryRequestTimeTests
	{
		const long Seconds19470101 = -725_846_400;
		const long Seconds19600101 = -315_619_200;
		const long Seconds20240301 = 1_709_251_200;

		[Test]
		public void Decoder_ReadsZeroAsTheDefaultDateTime()
		{
			var request = Decode(DtcWire.EncodeHistoricalPriceDataRequest(1, "fred-GNPCA", 0, 0));

			Assert.That(request.StartDateTime, Is.EqualTo(default(DateTime)));
			Assert.That(request.EndDateTime, Is.EqualTo(default(DateTime)));
		}

		[Test]
		public void Decoder_ReadsNegativeTimesAsDatesBefore1970()
		{
			var request = Decode(DtcWire.EncodeHistoricalPriceDataRequest(1, "fred-GNPCA", Seconds19470101, Seconds19600101));

			Assert.That(request.StartDateTime, Is.EqualTo(new DateTime(1947, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
			Assert.That(request.EndDateTime, Is.EqualTo(new DateTime(1960, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
			Assert.That(request.Symbol, Is.EqualTo("fred-GNPCA"));
			Assert.That(request.RecordInterval, Is.EqualTo(HistoricalDataIntervalEnum.INTERVAL_1_DAY));
		}

		[TestCase(0L, 0L, null, null)]
		[TestCase(Seconds19470101, 0L, "1947-01-01", null)]
		[TestCase(0L, Seconds19600101, null, "1960-01-01")]
		[TestCase(Seconds19470101, Seconds19600101, "1947-01-01", "1960-01-01")]
		[TestCase(Seconds19470101, Seconds20240301, "1947-01-01", "2024-03-01")]
		public async Task FredQuery_HasTheRequestedObservationRange(long start, long end, string? expectedStart, string? expectedEnd)
		{
			var (query, _) = await RequestHistoryAsync(start, end);

			Assert.That(query["series_id"], Is.EqualTo("GNPCA"));
			Assert.That(query["observation_start"], Is.EqualTo(expectedStart), "0 must leave the start open: the earliest observation.");
			Assert.That(query["observation_end"], Is.EqualTo(expectedEnd), "0 must leave the end open: the latest observation.");
			Assert.That(query["limit"], Is.EqualTo("922"));
		}

		[Test]
		public async Task ObservationsBefore1970_GoOutAsNegativeMicroseconds()
		{
			var (_, response) = await RequestHistoryAsync(Seconds19470101, Seconds19600101);

			// The fixture has four observations; the second is missing (".").
			Assert.That(response.Header.NoRecordsToReturn, Is.EqualTo(0));
			Assert.That(response.Records.Select(r => r.StartDateTime), Is.EqualTo(new[]
			{
				Seconds19470101 * 1_000_000,
				new DateTimeOffset(1947, 7, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds() * 1_000_000,
				Seconds19600101 * 1_000_000,
			}));
			Assert.That(response.Records.Select(r => r.LastPrice), Is.EqualTo(new[] { 243.164, 250.716, 542.648 }));
		}

		static HistoricalPriceDataRequest Decode(byte[] message)
		{
			var decoder = MessageProtocol.CreateMessageProtocol(EncodingEnum.BINARY_WITH_VARIABLE_LENGTH_STRINGS)
				.MessageDecoderFactory.CreateMessageDecoder(message);
			Assert.That(decoder.DecodeMessageType(), Is.EqualTo(DtcConstants.HISTORICAL_PRICE_DATA_REQUEST));
			return decoder.DecodeHistoricalPriceDataRequest();
		}

		/// <summary>Decodes a DTC request, reads its history through the FRED store, and streams the response.</summary>
		static async Task<(System.Collections.Specialized.NameValueCollection Query, HistoryResponse Response)> RequestHistoryAsync(long start, long end)
		{
			var request = Decode(DtcWire.EncodeHistoricalPriceDataRequest(5, "fred-GNPCA", start, end));
			var handler = new RecordingHandler(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fred-series-observations-pre1970.json")));
			using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.stlouisfed.org/fred/") };
			using var service = new Service(httpClient, (ServiceApiKey)"offline-test-key", NullLoggerFactory.Instance);
			using var store = new Store(service, NullLoggerFactory.Instance);
			using var reader = await store.CreateSymbolHistoryReaderAsync(
				new FakeSymbol("GNPCA"),
				HistoryInterval.Daily,
				request.StartDateTime,
				request.EndDateTime,
				922);

			var frames = new List<byte[]>();
			await HistoricalPriceDataStreamer.StreamAsync(
				reader,
				request.RequestId,
				request.RecordInterval,
				MessageProtocol.CreateMessageProtocol(MessageProtocol.PreferredEncoding).MessageEncoderFactory,
				bytes => frames.AddRange(DtcWire.SplitFrames(bytes)),
				NullLogger.Instance,
				CancellationToken.None);

			Assert.That(handler.Requests, Has.Count.EqualTo(1));
			var uri = handler.Requests[0];
			Assert.That(uri.AbsolutePath, Is.EqualTo("/fred/series/observations"));
			return (HttpUtility.ParseQueryString(uri.Query), DtcWire.DecodeHistoryResponse(frames));
		}

		sealed class RecordingHandler : HttpMessageHandler
		{
			readonly string _responseJson;

			public RecordingHandler(string responseJson)
			{
				_responseJson = responseJson;
			}

			public List<Uri> Requests { get; } = new();

			protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			{
				Requests.Add(request.RequestUri!);
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
				});
			}
		}
	}
}
