// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

#pragma warning disable CC0022
namespace SomeDataProvider.DataStorage.HistoryStores.Providers
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
	using Microsoft.Extensions.Options;

	using NBLib.DateTime;
	using NBLib.Logging;

	using SomeDataProvider.DataStorage.Definitions;

	public class SymbolHistoryTextFileStore : ISymbolHistoryStore
	{
		const string DailyDateTimeFormat = "yyyy-MM-dd";
		readonly ILoggerFactory _loggerFactory;
		readonly string _folderPath;

		public SymbolHistoryTextFileStore(IOptions<Options> opts, ILoggerFactory loggerFactory)
		{
			_loggerFactory = loggerFactory;
			_folderPath = opts.Value.FolderPath ?? Directory.GetCurrentDirectory();
		}

		public Task<ISymbolHistoryStoreReader> CreateSymbolHistoryReaderAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, CancellationToken cancellationToken = default)
		{
			return Task.FromResult((ISymbolHistoryStoreReader)new Reader(_folderPath, symbol, historyInterval, start, end, limit, _loggerFactory));
		}

		static DateTime ParseDateTimeFromLine(string line, HistoryInterval historyInterval)
		{
			if (historyInterval == HistoryInterval.Daily)
				return DateTime.ParseExact(line.AsSpan().Slice(0, 10), DailyDateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
			throw new NotImplementedException($"History interval '{historyInterval}' is not implemented for text file.");
		}

		public class Options
		{
			public string? FolderPath { get; set; }
		}

		class Reader : SymbolHistoryStoreReaderBase<Reader>
		{
			readonly string _folderPath;

			public Reader(string folderPath, ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, ILoggerFactory loggerFactory)
				: base(symbol, historyInterval, start, end, limit, loggerFactory)
			{
				_folderPath = folderPath;
			}

			public override async Task<SymbolHistoryResponse> ReadSymbolHistoryAsync(CancellationToken cancellationToken = default)
			{
				return await L.LogOperationAsync(async () =>
				{
					var start = Start;
					var end = End;
					if (ContinuationToken != null)
						start = DateTime.Parse(ContinuationToken, CultureInfo.InvariantCulture);

					if (end == DateTime.MinValue)
						end = DateTime.Today.ToUniversalTime();

					var filePath = Path.Combine(_folderPath, $"{Symbol.Code}-{HistoryInterval}.csv");
					if (!File.Exists(filePath))
						return SymbolHistoryResponse.Empty;

					var lines = await File.ReadAllLinesAsync(filePath, Encoding.ASCII, cancellationToken);
					var ln = lines.Length;
					if (ln < 2)
						return SymbolHistoryResponse.Empty;

					var symbolSettings = Symbol.DataServiceSettings?.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=').Select(y => y.Trim().ToLowerInvariant()).ToArray()).ToArray();
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
					var records = new List<SymbolHistoryRecord>(ln);
					if (fillGaps)
					{
						var lastDate = ParseDateTimeFromLine(lines[^1], HistoryInterval);
						if (lastDate < end)
							lastDate = end;

						var firstDate = ParseDateTimeFromLine(lines[1], HistoryInterval);
						if (firstDate >= start && firstDate <= end)
							records.Add(LineToRecord(firstDate, lines[1]));

						var lineIndex = 2;
						for (var currSequentialDate = firstDate.AddDays(1); currSequentialDate <= lastDate; currSequentialDate = currSequentialDate.AddDays(1))
						{
							var currLine = lineIndex < ln ? lines[lineIndex] : lines[lineIndex - 1];
							var currLineDate = ParseDateTimeFromLine(currLine, HistoryInterval);
							if (currSequentialDate == currLineDate)
							{
								if (currLineDate >= start && currLineDate <= end)
									records.Add(LineToRecord(currLineDate, currLine));
								if (lineIndex < ln)
									lineIndex++;
							}
							else
							{
								if (currSequentialDate.DayOfWeek != DayOfWeek.Saturday && currSequentialDate.DayOfWeek != DayOfWeek.Sunday)
								{
									if (currSequentialDate >= start && currSequentialDate <= end)
										records.Add(LineToRecord(currSequentialDate, lines[lineIndex - 1]));
								}
							}
							if (records.Count >= Limit)
							{
								ContinuationToken = (ContinuationToken)records[^1].TimeStamp.ToIsoString();
								L.LogDebug("ContinuationToken: {continuationToken}", ContinuationToken);
								return new SymbolHistoryResponse(records, true);
							}
						}
					}
					else
					{
						throw new NotImplementedException("Non-fillGaps is not implemented.");
					}
					return new SymbolHistoryResponse(records, default);

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
				}, "ReadSymbolHistory({symbol},{historyInterval},{limit})", Symbol.Code, HistoryInterval, Limit);
			}
		}
	}
}