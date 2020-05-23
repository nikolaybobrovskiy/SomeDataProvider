// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.InMem
{
	using System;
	using System.Threading;
	using System.Threading.Tasks;

	using NBLib.Enum;

	using SomeDataProvider.DataStorage.Definitions;

	// TODO: Cache.
	public class SymbolsStore : ISymbolsStore, IDisposable
	{
		const char DataSourceSymbolSeparator = '-';
		readonly Fred.Service _fredService;

		public SymbolsStore()
		{
			// TODO: From secret.
			_fredService = new Fred.Service((Fred.ServiceApiKey)"5e34dec427a5c32c3e45a70604b85459"!);
		}

		public void Dispose()
		{
			_fredService.Dispose();
		}

		public async Task<ISymbol?> GetSymbolAsync(string code, CancellationToken cancellationToken = default)
		{
			switch (code)
			{
				case var _ when code == $"{DataSources.CentralBankOfRussia}{DataSourceSymbolSeparator}KeyRate":
					return new Symbol(code)
					{
						Description = "CBR Key Rate",
						Category = SymbolCategories.CentralBanksRates,
						NumberOfDecimals = 2,
						DataService = DataService.TextFile,
						DataServiceSettings = "FillDailyGaps=true;",
					};
				// stb-infl.m.Russia
				// stb-infl.y.Russia
				case var _ when code.StartsWith($"{DataSources.StatBureau}{DataSourceSymbolSeparator}{DataSources.StatBureauInflationPrefix}.", StringComparison.Ordinal):
					{
						var country = code.GetStatBureauInflationCountry();
						var periodicity = code.GetStatBureauInflationPeriodicity();
						return new Symbol(code)
						{
							Description = $"Inflation {country} ({(periodicity == 'm' ? "m/m" : "y/y")})",
							Category = SymbolCategories.EconomicsInflation,
							NumberOfDecimals = 2,
							DataService = DataService.StatBureau,
						};
					}
				// fred-<seriesId>[.<units>]
				// Example: fred-RUSCPIALLMINMEI.pc1
				case var _ when code.StartsWith($"{DataSources.Fred}{DataSourceSymbolSeparator}", StringComparison.Ordinal):
					{
						var fredSymbol = code.GetFredSymbol();
						var seriesInfo = await _fredService.GetSeriesInfoAsync(fredSymbol.SeriesId, cancellationToken);
						if (seriesInfo == null) return null;
						return new Symbol(code)
						{
							Description = $"{seriesInfo.Title} ({seriesInfo.UnitsShort}/{seriesInfo.FrequencyShort}/{seriesInfo.SeasonalAdjustmentShort})"
								+ (fredSymbol.Units != Fred.DataValueTransformation.NoTransformation ? $" {fredSymbol.Units.GetDescription()}" : string.Empty),
							Category = SymbolCategories.Economics,
							DataService = DataService.Fred,
							NumberOfDecimals = 2,
						};
					}
			}
			return default;
		}

		class Symbol : ISymbol
		{
			public Symbol(string code)
			{
				Code = code;
			}

			// ReSharper disable UnusedAutoPropertyAccessor.Local
			public string Code { get; set; }

			public string? Exchange { get; set; }

			public SymbolType Type { get; set; }

			public string? Description { get; set; }

			public string? Category { get; set; }

			public int NumberOfDecimals { get; set; }

			public string? Currency { get; set; }

			public bool IsRealTime { get; set; }

			public bool IsDelayed { get; set; }

			public bool IsDiscontinued { get; set; }

			public DataService DataService { get; set; }

			public string? DataServiceSettings { get; set; }
			// ReSharper restore UnusedAutoPropertyAccessor.Local
		}
	}
}