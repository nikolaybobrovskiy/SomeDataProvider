// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.InMem
{
	using System;
	using System.Threading;
	using System.Threading.Tasks;

	using SomeDataProvider.DataStorage.Definitions;

	public class SymbolsStore : ISymbolsStore
	{
		const char DataSourceSymbolSeparator = '-';

		public Task<ISymbol?> GetSymbolAsync(string code, CancellationToken cancellationToken)
		{
			switch (code)
			{
				case var _ when code == $"{DataSources.CentralBankOfRussia}{DataSourceSymbolSeparator}KeyRate":
					return Task.FromResult((ISymbol?)new Symbol(code)
					{
						Description = "CBR Key Rate",
						Category = SymbolCategories.CentralBanksRates,
						NumberOfDecimals = 2,
						DataService = DataService.TextFile,
						DataServiceSettings = "FillDailyGaps=true;",
					});
				case var _ when code.StartsWith($"{DataSources.StatBureau}{DataSourceSymbolSeparator}{DataSources.StatBureauInflationPrefix}.", StringComparison.Ordinal):
					{
						var country = code.GetStatBureauInflationCountry();
						var periodicity = code.GetStatBureauInflationPeriodicity();
						return Task.FromResult((ISymbol?)new Symbol(code)
						{
							Description = $"Inflation {country} ({(periodicity == 'm' ? "m/m" : "y/y")})",
							Category = SymbolCategories.MacroeconomicsInflation,
							NumberOfDecimals = 2,
							DataService = DataService.StatBureau,
						});
					}
			}
			return Task.FromResult((ISymbol?)null);
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