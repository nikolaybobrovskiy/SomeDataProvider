// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.HistoryStores
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.IO;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	using Microsoft.Extensions.Logging;

	using NBLib.Logging;

	using SomeDataProvider.DataStorage.Definitions;

	public class SymbolHistoryTextFileStore : ISymbolHistoryStore
	{
		const string DailyDateTimeFormat = "yyyy-MM-dd";
		readonly string _folderPath;

		public SymbolHistoryTextFileStore(string folderPath, ILoggerFactory loggerFactory)
		{
			_folderPath = folderPath;
			L = loggerFactory.CreateLogger<SymbolHistoryTextFileStore>();
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
				var filePath = Path.Combine(_folderPath, $"{symbol.Code}-{historyInterval}.csv");
				if (!File.Exists(filePath))
				{
					return GetSymbolHistoryResult.Empty;
				}
				var lines = await File.ReadAllLinesAsync(filePath, Encoding.ASCII, cancellationToken);
				var ln = lines.Length;
				if (ln < 2)
				{
					return GetSymbolHistoryResult.Empty;
				}
				var symbolSettings = symbol.DataServiceSettings?.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=').Select(y => y.Trim().ToLowerInvariant()).ToArray()).ToArray();
				var fillGaps = symbolSettings?.Any(x => x[0] == "filldailygaps" && bool.TryParse(x[1], out var v) && v) == true;
				var header = lines[0].Split(';');
				int lastPriceIndex = -1, openPriceIndex = -1, highPriceIndex = -1, lowPriceIndex = -1, volumeIndex = -1;
				for (var i = 0; i < header.Length; i++)
				{
					switch (header[i])
					{
						case "Close":
							lastPriceIndex = i;
							break;
						case "Open":
							openPriceIndex = i;
							break;
						case "High":
							highPriceIndex = i;
							break;
						case "Low":
							lowPriceIndex = i;
							break;
						case "Volume":
							volumeIndex = i;
							break;
					}
				}
				var records = new List<SymbolHistoryRecord>(ln - 1);
				if (fillGaps)
				{
					var firstDate = ParseDateTimeFromLine(lines[1], historyInterval);
					var lastDate = ParseDateTimeFromLine(lines[^1], historyInterval);
					records.Add(LineToRecord(firstDate, lines[1]));
					var lineIndex = 2;
					for (var currSequentialDate = firstDate.AddDays(1); currSequentialDate <= lastDate; currSequentialDate = currSequentialDate.AddDays(1))
					{
						var currLine = lines[lineIndex];
						var currLineDate = ParseDateTimeFromLine(currLine, historyInterval);
						if (currSequentialDate == currLineDate)
						{
							if (currLineDate >= start && currLineDate <= end)
							{
								records.Add(LineToRecord(currLineDate, currLine));
							}
							lineIndex++;
						}
						else
						{
							if (currSequentialDate.DayOfWeek != DayOfWeek.Saturday && currSequentialDate.DayOfWeek != DayOfWeek.Sunday)
							{
								if (currSequentialDate >= start && currSequentialDate <= end)
								{
									records.Add(LineToRecord(currSequentialDate, lines[lineIndex - 1]));
								}
							}
						}
						if (records.Count >= limit)
						{
							return new GetSymbolHistoryResult(records, records[^1].TimeStamp.ToString("o", CultureInfo.InvariantCulture));
						}
					}
				}
				else
				{
					throw new NotImplementedException("Non-fillGaps is not implemented.");
				}
				return new GetSymbolHistoryResult(records, null);

				SymbolHistoryRecord LineToRecord(DateTime date, string line)
				{
					var values = line.Split(';');
					return new SymbolHistoryRecord(
						date,
						openPriceIndex >= 0 ? double.Parse(values[openPriceIndex], CultureInfo.InvariantCulture) : 0,
						highPriceIndex >= 0 ? double.Parse(values[highPriceIndex], CultureInfo.InvariantCulture) : 0,
						lowPriceIndex >= 0 ? double.Parse(values[lowPriceIndex], CultureInfo.InvariantCulture) : 0,
						lastPriceIndex >= 0 ? double.Parse(values[lastPriceIndex], CultureInfo.InvariantCulture) : 0,
						volumeIndex >= 0 ? double.Parse(values[volumeIndex], CultureInfo.InvariantCulture) : 0);
				}
			}, "GetSymbolHistory({symbol},{historyInterval},{limit})", symbol.Code, historyInterval, limit);
		}

		static DateTime ParseDateTimeFromLine(string line, HistoryInterval historyInterval)
		{
			if (historyInterval == HistoryInterval.Daily)
				return DateTime.ParseExact(line.AsSpan().Slice(0, 10), DailyDateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
			throw new NotImplementedException($"History interval '{historyInterval}' is not implemented for text file.");
		}
	}
}