// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;

	using SomeDataProvider.DataStorage.Definitions;

	/// <summary>
	/// Stores for a session test: like the server's StoresProvider, a "fred-" symbol goes to a store that knows the code
	/// without the prefix. The store knows one series, <see cref="SeriesId"/>, plus a small fixed catalog for symbol
	/// search, independent of SeriesId.
	/// </summary>
	sealed class FakeStores : ISymbolsStoreProvider, ISymbolsStore, ISymbolHistoryStoreProvider, ISymbolHistoryStore
	{
		public const string SeriesId = "GDP";

		/// <summary>A SearchText that makes a search throw, simulating FRED being unreachable or returning an error.</summary>
		public const string FailingSearchText = "FAIL";

		const string Prefix = "fred-";

		// A small fixed catalog a search matches against; unrelated to GetSymbolAsync's single known series.
		static readonly (string Code, string Description)[] SearchCatalog =
		{
			("GDP", "Gross Domestic Product"),
			("GDPC1", "Real Gross Domestic Product"),
			("UNRATE", "Unemployment Rate"),
		};

		readonly FakeHistory _history;

		public FakeStores(FakeHistory history)
		{
			_history = history;
		}

		/// <summary>The limit the server's last SearchSymbolsAsync call passed, for asserting the documented cap.</summary>
		public int? LastSearchLimit { get; private set; }

		public Func<int, ISymbolHistoryStoreReader>? HistoryReaderFactory { get; set; }

		public ValueTask<(ISymbolsStore SymbolsStore, string SymbolCode)?> GetSymbolsStoreAsync(string code, CancellationToken cancellationToken = default)
		{
			(ISymbolsStore, string)? result = code.StartsWith(Prefix, StringComparison.Ordinal) ? (this, code.Substring(Prefix.Length)) : null;
			return ValueTask.FromResult(result);
		}

		public ValueTask<ISymbol?> GetSymbolAsync(string code, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(code == SeriesId ? (ISymbol?)new FakeSymbol(SeriesId) : null);

		ValueTask<IReadOnlyCollection<ISymbol>> ISymbolsStoreProvider.GetKnownSymbolsAsync(CancellationToken cancellationToken) =>
			ValueTask.FromResult<IReadOnlyCollection<ISymbol>>(Array.Empty<ISymbol>());

		ValueTask<IReadOnlyCollection<ISymbol>> ISymbolsStore.GetKnownSymbolsAsync(CancellationToken cancellationToken) =>
			ValueTask.FromResult<IReadOnlyCollection<ISymbol>>(Array.Empty<ISymbol>());

		ValueTask<IReadOnlyCollection<ISymbol>> ISymbolsStoreProvider.SearchSymbolsAsync(string searchText, SymbolSearchType searchType, int limit, CancellationToken cancellationToken) =>
			SearchSymbolsAsync(searchText, searchType, limit);

		ValueTask<IReadOnlyCollection<ISymbol>> ISymbolsStore.SearchSymbolsAsync(string searchText, SymbolSearchType searchType, int limit, CancellationToken cancellationToken) =>
			SearchSymbolsAsync(searchText, searchType, limit);

		public ValueTask<ISymbolHistoryStore> GetSymbolHistoryStoreAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<ISymbolHistoryStore>(this);

		public ValueTask<ISymbolHistoryStoreReader> CreateSymbolHistoryReaderAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(HistoryReaderFactory?.Invoke(limit) ?? _history.CreateReader(limit));

		ValueTask<IReadOnlyCollection<ISymbol>> SearchSymbolsAsync(string searchText, SymbolSearchType searchType, int limit)
		{
			LastSearchLimit = limit;
			if (searchText == FailingSearchText)
				throw new InvalidOperationException("Simulated FRED failure.");
			IReadOnlyCollection<ISymbol> matches = SearchCatalog
				.Where(s => searchType == SymbolSearchType.BySymbol
					? s.Code.Contains(searchText, StringComparison.OrdinalIgnoreCase)
					: s.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase))
				.Take(limit)
				.Select(s => (ISymbol)new FakeSymbol(s.Code))
				.ToArray();
			return ValueTask.FromResult(matches);
		}
	}
}
