// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.InMem
{
	using System;
	using System.Collections.Generic;
	using System.Threading;
	using System.Threading.Tasks;

	using Microsoft.Extensions.Logging;

	using NBLib.Enum;

	using SomeDataProvider.DataStorage.Definitions;

	// How to populate symbols list from server.
	// https://www.sierrachart.com/index.php?page=doc/DTC_TestClient.php#PopulatingSymbolList

	// TODO: Cache.
	public sealed class SymbolsStore : ISymbolsStore, IDisposable
	{
		const char DataSourceSymbolSeparator = '-';
		readonly Fred.Service _fredService;

		public SymbolsStore(ILoggerFactory loggerFactory)
		{
			// TODO: From secret.
			_fredService = new Fred.Service((Fred.ServiceApiKey)"5e34dec427a5c32c3e45a70604b85459", loggerFactory);
		}

		public void Dispose()
		{
			_fredService.Dispose();
		}

		// TODO: Cache
		public async ValueTask<ISymbol?> GetSymbolAsync(string code, CancellationToken cancellationToken = default)
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
				// units:
				//   lin = Levels (No transformation)
				//   chg = Change
				//   ch1 = Change from Year Ago
				//   pch = Percent Change
				//   pc1 = Percent Change from Year Ago
				//   pca = Compounded Annual Rate of Change
				//   cch = Continuously Compounded Rate of Change
				//   cca = Continuously Compounded Annual Rate of Change
				//   log = Natural Log
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
							MinPriceIncrement = 0.01F,
						};
					}
			}
			return default;
		}

		public ValueTask<IReadOnlyCollection<ISymbol>> GetKnownSymbolsAsync(CancellationToken cancellationToken = default)
		{
			return new ValueTask<IReadOnlyCollection<ISymbol>>(Array.Empty<ISymbol>());
			//// var result = new Symbol[]
			//// {
			//// 	new ("fred-RUSCPIALLMINMEI")
			//// 	{
			//// 		Description = "RU CPI Value",
			//// 		Category = SymbolCategories.Economics,
			//// 		DataService = DataService.Fred,
			//// 		NumberOfDecimals = 2,
			//// 		MinPriceIncrement = 0.01F,
			//// 	}
			//// };
			//// return new ValueTask<IReadOnlyCollection<ISymbol>>(result);
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

			public float MinPriceIncrement { get; set; }

			public string? Currency { get; set; }

			public bool IsRealTime { get; set; }

			public bool IsDelayed { get; set; }

			public bool IsDiscontinued { get; set; }

			public DataService DataService { get; set; }

			public string? DataServiceSettings { get; set; }
			// ReSharper restore UnusedAutoPropertyAccessor.Local

			public override string ToString()
			{
				return Code;
			}
		}
	}
}