// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Collections.Generic;
	using System.Threading;
	using System.Threading.Tasks;

	using SomeDataProvider.DataStorage.Definitions;

	/// <summary>
	/// Stores for a session test: like the server's StoresProvider, a "fred-" symbol goes to a store that knows the code
	/// without the prefix. The store knows one series, <see cref="SeriesId"/>.
	/// </summary>
	sealed class FakeStores : ISymbolsStoreProvider, ISymbolsStore, ISymbolHistoryStoreProvider, ISymbolHistoryStore
	{
		public const string SeriesId = "GDP";
		const string Prefix = "fred-";

		readonly FakeHistory _history;

		public FakeStores(FakeHistory history)
		{
			_history = history;
		}

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

		public ValueTask<ISymbolHistoryStore> GetSymbolHistoryStoreAsync(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<ISymbolHistoryStore>(this);

		public ValueTask<ISymbolHistoryStoreReader> CreateSymbolHistoryReaderAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(_history.CreateReader(limit));
	}
}
