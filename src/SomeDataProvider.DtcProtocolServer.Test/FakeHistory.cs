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
	/// A series of daily observations, paged as the FRED store pages them: each page holds up to the batch size of
	/// observations, missing values are dropped from the page's records, and a page reports more pages when it is full.
	/// </summary>
	sealed class FakeHistory
	{
		public const int BatchSize = 922;

		public static readonly DateTime FirstDate = new(1940, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		readonly int _observationsCount;
		readonly Func<int, bool> _isMissing;

		public FakeHistory(int observationsCount, Func<int, bool>? isMissing = null)
		{
			_observationsCount = observationsCount;
			_isMissing = isMissing ?? (_ => false);
		}

		public int ReadCount { get; private set; }

		public IEnumerable<int> RecordIndexes => Enumerable.Range(0, _observationsCount).Where(i => !_isMissing(i));

		public static DateTime DateOf(int observationIndex) => FirstDate.AddDays(observationIndex);

		public static double ValueOf(int observationIndex) => 100 + (observationIndex * 0.25);

		public ISymbolHistoryStoreReader CreateReader(int limit = BatchSize) => new Reader(this, limit);

		sealed class Reader : ISymbolHistoryStoreReader
		{
			readonly FakeHistory _history;
			readonly int _limit;
			int _offset;
			bool _ended;

			public Reader(FakeHistory history, int limit)
			{
				_history = history;
				_limit = limit;
			}

			public Task<SymbolHistoryResponse> ReadSymbolHistoryAsync(CancellationToken cancellationToken = default)
			{
				if (_ended)
					throw new InvalidOperationException("The history was read after its last page.");
				_history.ReadCount++;
				var pageObservations = Math.Max(0, Math.Min(_limit, _history._observationsCount - _offset));
				var records = Enumerable.Range(_offset, pageObservations)
					.Where(i => !_history._isMissing(i))
					.Select(i => (ISymbolHistoryRecord)new Record(DateOf(i), ValueOf(i)))
					.ToArray();
				_offset += pageObservations;
				var hasMore = pageObservations == _limit;
				_ended = !hasMore;
				return Task.FromResult(new SymbolHistoryResponse(records, hasMore));
			}

			public void Dispose()
			{
			}
		}

		sealed class Record : ISymbolHistoryRecord
		{
			public Record(DateTime timeStamp, double value)
			{
				TimeStamp = timeStamp;
				OpenPrice = HighPrice = LowPrice = LastPrice = value;
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
