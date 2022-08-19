// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Fred
{
	using System;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;

	using Microsoft.Extensions.Logging;

	using NBLib.Enum;
	using NBLib.Logging;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DataStorage.Fred.Dto;

	public sealed class Store : ISymbolHistoryStore, ISymbolsStore, IDisposable
	{
		readonly ILoggerFactory _loggerFactory;
		readonly Service _fredService;
		readonly bool _disposeFredService;
		readonly ILogger<Store> _l;

		public Store(ServiceApiKey fredServiceApiKey, ILoggerFactory loggerFactory)
			: this(new Service(fredServiceApiKey, loggerFactory), loggerFactory)
		{
			_disposeFredService = true;
		}

		public Store(Service fredService, ILoggerFactory loggerFactory)
		{
			_loggerFactory = loggerFactory;
			_fredService = fredService;
			_l = loggerFactory.CreateLogger<Store>();
		}

		public ValueTask<ISymbolHistoryStoreReader> CreateSymbolHistoryReaderAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, CancellationToken cancellationToken = default)
		{
#pragma warning disable CC0022 // Should dispose object
			return ValueTask.FromResult((ISymbolHistoryStoreReader)new Reader(_fredService, symbol, historyInterval, start, end, limit, _loggerFactory));
#pragma warning restore CC0022
		}

		//// public Task<ETag> GetActualETag(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default)
		//// {
		//// 	return Task.FromResult(GetActualETag());
		//// }

		public async ValueTask<ISymbol?> GetSymbolAsync(string code, CancellationToken cancellationToken = default)
		{
			return await _l.LogOperationAsync(async () =>
			{
				var fredSymbol = GetFredSymbol(code);
				var seriesInfo = await _fredService.GetSeriesInfoAsync(fredSymbol.SeriesId, cancellationToken);
				if (seriesInfo == null) return null;
				return new Symbol(fredSymbol.SeriesId, fredSymbol.Units, seriesInfo);
			}, "GetSymbol({code})", code);
		}

		public async ValueTask<IReadOnlyCollection<ISymbol>> GetKnownSymbolsAsync(CancellationToken cancellationToken = default)
		{
			return await _l.LogOperationAsync(async () =>
			{
				var allSeries = await _fredService.GetAllSeriesAsync(cancellationToken);
				return allSeries.Select(seriesInfo => new Symbol(
					seriesInfo.Id,
					DataValueTransformation.NoTransformation,
					seriesInfo)).ToArray();
			}, "GetKnownSymbols()");
		}

		public void Dispose()
		{
			if (_disposeFredService)
				_fredService.Dispose();
		}

		static FredSymbol GetFredSymbol(string code)
		{
			var normalizedCode = code.AsSpan();
			var units = DataValueTransformation.NoTransformation;
			SeriesInfoId seriesId;
			var optionsSeparatorIndex = normalizedCode.IndexOf('.');
			if (optionsSeparatorIndex >= 0)
			{
				units = normalizedCode.Slice(optionsSeparatorIndex + 1).ToEnumAsStringValue<DataValueTransformation>();
				seriesId = (SeriesInfoId)normalizedCode.Slice(0, optionsSeparatorIndex).ToString();
			}
			else
			{
				seriesId = (SeriesInfoId)normalizedCode.ToString();
			}
			return new FredSymbol(seriesId, units);
		}

		//// static ETag GetActualETag()
		//// {
		//// 	// TODO: ETag will be last release date.
		//// 	return (ETag)DateTime.UtcNow.RoundUp(TimeSpan.FromHours(2)).ToIsoString();
		//// }

		internal sealed class FredSymbol : IEquatable<FredSymbol>
		{
			internal FredSymbol(SeriesInfoId seriesId, DataValueTransformation units = DataValueTransformation.NoTransformation)
			{
				SeriesId = seriesId;
				Units = units;
			}

			public SeriesInfoId SeriesId { get; }

			public DataValueTransformation Units { get; }

			public static bool operator ==(FredSymbol? left, FredSymbol? right)
			{
				return Equals(left, right);
			}

			public static bool operator !=(FredSymbol? left, FredSymbol? right)
			{
				return !Equals(left, right);
			}

			public bool Equals(FredSymbol? other)
			{
				if (ReferenceEquals(null, other))
					return false;
				if (ReferenceEquals(this, other))
					return true;
				return SeriesId == other.SeriesId && Units == other.Units;
			}

			public override bool Equals(object? obj)
			{
				return ReferenceEquals(this, obj) || obj is FredSymbol other && Equals(other);
			}

			public override int GetHashCode()
			{
				return HashCode.Combine(SeriesId, (int)Units);
			}
		}

		class Symbol : ISymbol
		{
			public Symbol(SeriesInfoId fredSeriesId, DataValueTransformation units, CategorizedSeriesInfo seriesInfo)
				: this(fredSeriesId, units, (SeriesInfo)seriesInfo)
			{
				Category = seriesInfo.Category;
			}

			public Symbol(SeriesInfoId fredSeriesId, DataValueTransformation units, SeriesInfo seriesInfo)
				: this(fredSeriesId, units)
			{
				Description = $"{seriesInfo.Title} ({seriesInfo.UnitsShort}/{seriesInfo.FrequencyShort}/{seriesInfo.SeasonalAdjustmentShort})"
					+ (units != DataValueTransformation.NoTransformation ? $" {units.GetDescription()}" : string.Empty);
				IsDiscontinued = seriesInfo.IsDiscontinued;
			}

			Symbol(SeriesInfoId fredSeriesId, DataValueTransformation units)
			{
				Code = fredSeriesId
					+ (units != DataValueTransformation.NoTransformation ? "." + units.GetStringValue() : string.Empty);
			}

			public string Code { get; }

			public string? Exchange => null;

			public SymbolType Type => SymbolType.Unset;

			public string? Description { get; }

			public string? Category { get; }

			public int NumberOfDecimals => 2;

			public float MinPriceIncrement => 0.01F;

			public string? Currency => null;

			public bool IsRealTime => false;

			public bool IsDelayed => true;

			public bool IsDiscontinued { get; }

			public DataService DataService => DataService.Fred;

			public string? DataServiceSettings => null;

			public override string ToString()
			{
				return Code;
			}
		}

		class Reader : SymbolHistoryStoreReaderBase<Reader>
		{
			readonly Service _fredService;

			public Reader(Service fredService, ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, ILoggerFactory loggerFactory)
				: base(symbol, historyInterval, start, end, limit, loggerFactory)
			{
				_fredService = fredService;
			}

			public override async Task<SymbolHistoryResponse> ReadSymbolHistoryAsync(CancellationToken cancellationToken = default)
			{
				var fredSymbol = GetFredSymbol(Symbol.Code);

				var offset = 0;
				if (ContinuationToken != null)
					offset = int.Parse(ContinuationToken);

				var observationsData = await _fredService.GetObservationsAsync(fredSymbol.SeriesId, start: Start, end: End, transformation: fredSymbol.Units, offset: offset, limit: Limit, cancellationToken: cancellationToken);
				if (observationsData == null) return SymbolHistoryResponse.Empty;

				var newContinuationToken = observationsData.Observations.Length < Limit ? default : (ContinuationToken)(offset + Limit).ToString();
				ContinuationToken = newContinuationToken;
				return new SymbolHistoryResponse(observationsData.Observations.Where(x => x.Value != null).Select(x =>
				{
					var v = x.Value!.Value;
					// TODO: RevisablePeriod = two last values of the most available in service.
					return new SymbolHistoryRecord(x.Date, v);
				}).ToArray(), newContinuationToken != default);
			}

			sealed class SymbolHistoryRecord : ISymbolHistoryRecord
			{
				public SymbolHistoryRecord(
					DateTime timeStamp,
					double lastPrice)
				{
					TimeStamp = timeStamp;
					OpenPrice = lastPrice;
					HighPrice = lastPrice;
					LowPrice = lastPrice;
					LastPrice = lastPrice;
				}

				public DateTime TimeStamp { get; }

				public double OpenPrice { get; }

				public double HighPrice { get; }

				public double LowPrice { get; }

				public double LastPrice { get; }

				public double Volume => 0;
			}
		}
	}
}