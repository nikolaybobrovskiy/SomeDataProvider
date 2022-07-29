// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Definitions
{
	using System;
	using System.Threading;
	using System.Threading.Tasks;

	using Microsoft.Extensions.Logging;

	public interface ISymbolHistoryStore
	{
		Task<ISymbolHistoryStoreReader> CreateSymbolHistoryReaderAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, CancellationToken cancellationToken = default);
	}

	public interface ISymbolHistoryStoreReader : IDisposable
	{
		Task<SymbolHistoryResponse> ReadSymbolHistoryAsync(CancellationToken cancellationToken = default);
	}

	public abstract class SymbolHistoryStoreReaderBase<T> : ISymbolHistoryStoreReader
		where T : SymbolHistoryStoreReaderBase<T>
	{
		protected SymbolHistoryStoreReaderBase(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, ILoggerFactory loggerFactory)
		{
			Symbol = symbol;
			HistoryInterval = historyInterval;
			Start = start;
			End = end;
			Limit = limit;
			L = loggerFactory.CreateLogger<T>();
		}

		~SymbolHistoryStoreReaderBase()
		{
			Dispose(false);
		}

		protected ISymbol Symbol { get; }

		protected HistoryInterval HistoryInterval { get; }

		protected DateTime Start { get; }

		protected DateTime End { get; }

		protected int Limit { get; }

		protected ILogger<T> L { get; }

		protected ContinuationToken? ContinuationToken { get; set; }

		public abstract Task<SymbolHistoryResponse> ReadSymbolHistoryAsync(CancellationToken cancellationToken = default);

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void ReleaseUnmanagedResources()
		{
		}

		protected virtual void ReleaseManagedResources()
		{
		}

		protected virtual void Dispose(bool disposing)
		{
			ReleaseUnmanagedResources();
			if (disposing)
			{
				ReleaseManagedResources();
			}
		}
	}
}