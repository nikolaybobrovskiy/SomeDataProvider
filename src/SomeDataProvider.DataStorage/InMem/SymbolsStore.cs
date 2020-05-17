namespace SomeDataProvider.DataStorage.InMem
{
	using System.Threading;
	using System.Threading.Tasks;

	using SomeDataProvider.DataStorage.Definitions;

	public class SymbolsStore : ISymbolsStore
	{
		public Task<ISymbol?> GetSymbolAsync(string code, CancellationToken cancellationToken)
		{
			switch (code)
			{
				case "cbr-KeyRate":
					return Task.FromResult((ISymbol?)new Symbol(code)
					{
						Description = "CBR Key Rate",
						Category = "Central Banks Rates",
						NumberOfDecimals = 2,
						DataService = DataService.TextFile,
					});
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

			public DataService DataService { get; set; }
			// ReSharper restore UnusedAutoPropertyAccessor.Local
		}
	}
}