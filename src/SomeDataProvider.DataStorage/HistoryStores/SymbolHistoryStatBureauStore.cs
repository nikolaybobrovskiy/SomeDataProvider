// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.HistoryStores
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.IO;
	using System.Net.Http;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	using Microsoft.Extensions.Logging;

	using NBLib.Logging;
	using NBLib.Tasks;

	using Newtonsoft.Json;
	using Newtonsoft.Json.Linq;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DataStorage.InMem;

	// symbol.Code:
	// stb-infl.m.Russia
	// stb-infl.y.Russia
	public class SymbolHistoryStatBureauStore : ISymbolHistoryStore, IDisposable
	{
		const string MonthValueFormat = "yyyy-MM-dd";
		const string Url = "https://www.statbureau.org/";
		readonly HttpClient _httpClient;

		public SymbolHistoryStatBureauStore(ILoggerFactory loggerFactory)
		{
			L = loggerFactory.CreateLogger<SymbolHistoryTextFileStore>();
			_httpClient = new HttpClient { BaseAddress = new Uri(Url) };
		}

		ILogger<SymbolHistoryTextFileStore> L { get; }

		public async Task<GetSymbolHistoryResult> GetSymbolHistoryAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, string? continuationToken, CancellationToken cancellationToken = default)
		{
			return await L.LogOperationAsync(async () =>
			{
				if (continuationToken != null)
				{
					start = DateTime.Parse(continuationToken, CultureInfo.InvariantCulture);
				}
				if (end == DateTime.MinValue)
				{
					end = DateTime.Today.ToUniversalTime();
				}
				var country = symbol.Code.GetStatBureauInflationCountry().ToLowerInvariant();
				var periodicity = symbol.Code.GetStatBureauInflationPeriodicity();
				using var requestMessage = new HttpRequestMessage(HttpMethod.Get, $"get-data-json?country={country}");
				using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
				JToken? responseBody = null;
				if (response.Content.Headers.ContentType?.MediaType.ToLowerInvariant().EndsWith("/json") == true)
				{
					var responseStream = LiveStream.Start(async s =>
					{
						// ReSharper disable once AccessToDisposedClosure
						await response.Content.CopyToAsync(s).ConfigureAwait(false);
					});
					using var w = new StreamReader(responseStream, Encoding.UTF8, false, 1024, true);
					using var jr = new JsonTextReader(w);
					responseBody = await JToken.ReadFromAsync(jr, cancellationToken);
				}
				if (!response.IsSuccessStatusCode)
				{
					throw new InvalidOperationException($"Could not get data from {Url}: {response.StatusCode}.");
				}
				if (!(responseBody is JArray inflRecords))
				{
					throw new InvalidOperationException($"Unexpected JSON data from {Url}. Array was expected, but got {responseBody?.Type.ToString() ?? "non-JSON"}.");
				}
				// inflRecords
				var ln = inflRecords.Count;
				var records = new List<SymbolHistoryRecord>(ln);
				var rawValues = new double[ln];
				var prevMonth = DateTime.MinValue;
				for (var i = ln - 1; i >= 0; i--) // Service returns new values first.
				{
					var jsonRec = (JObject)inflRecords[i];
					var monthFormattedText = jsonRec["MonthFormatted"]?.ToObject<string>();
					if (monthFormattedText == null) continue;
					var month = DateTime.ParseExact(monthFormattedText, MonthValueFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
					if (periodicity == 'y')
					{
						if (prevMonth != DateTime.MinValue)
						{
							if (prevMonth.AddMonths(1) != month) throw new InvalidOperationException($"Values should go strictly month after month, but service returned: {prevMonth:MonthValueFormat} => {month:MonthValueFormat}.");
						}
					}
					prevMonth = month;
					if (month < start || month > end) continue;
					var v = jsonRec["InflationRate"]?.ToObject<double?>();
					if (v == null) throw new InvalidOperationException($"No value for index = {i}.");
					rawValues[i] = v.Value;
					if (periodicity == 'y')
					{
						if (i + 11 >= ln) continue;
						double yearChange = 1;
						for (var j = 0; j <= 11; j++)
						{
							yearChange *= 1 + rawValues[j + i] / 100;
						}
						v = (yearChange - 1) * 100;
					}
					records.Add(new SymbolHistoryRecord(
						month,
#pragma warning disable 8629 // False alarm. v is already checked for null.
						v.Value,
#pragma warning restore 8629
						v.Value,
						v.Value,
						v.Value,
						0));
					if (records.Count >= limit)
					{
						return new GetSymbolHistoryResult(records, records[^1].TimeStamp.ToString("o", CultureInfo.InvariantCulture));
					}
				}
				return new GetSymbolHistoryResult(records, null);
			}, "GetSymbolHistory({symbol},{historyInterval},{limit})", symbol.Code, historyInterval, limit);
		}

		public void Dispose()
		{
			_httpClient.Dispose();
		}
	}
}