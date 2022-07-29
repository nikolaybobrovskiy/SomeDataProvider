// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.HistoryStores.Providers
{
	using System;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;

	using Microsoft.Extensions.Logging;

	using NBLib.DateTime;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DataStorage.Fred;
	using SomeDataProvider.DataStorage.InMem;

	public sealed class SymbolHistoryFredStore : ICacheableSymbolHistoryStore, IDisposable
	{
		readonly ILoggerFactory _loggerFactory;
		readonly Service _fredService;
		readonly bool _disposeFredService;

		public SymbolHistoryFredStore(ServiceApiKey fredServiceApiKey, ILoggerFactory loggerFactory)
		{
			_loggerFactory = loggerFactory;
			_fredService = new Service(fredServiceApiKey, loggerFactory);
			_disposeFredService = true;
		}

		public SymbolHistoryFredStore(Service fredService, ILoggerFactory loggerFactory)
		{
			_loggerFactory = loggerFactory;
			_fredService = fredService;
		}

		public Task<ISymbolHistoryStoreReader> CreateSymbolHistoryReaderAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, CancellationToken cancellationToken = default)
		{
#pragma warning disable CC0022 // Should dispose object
			return Task.FromResult((ISymbolHistoryStoreReader)new Reader(_fredService, symbol, historyInterval, start, end, limit, _loggerFactory));
#pragma warning restore CC0022
		}

		public Task<ETag> GetActualETag(ISymbol symbol, HistoryInterval historyInterval, CancellationToken cancellationToken = default)
		{
			return Task.FromResult(GetActualETag());
		}

		public void Dispose()
		{
			if (_disposeFredService)
				_fredService.Dispose();
		}

		static ETag GetActualETag()
		{
			// TODO: ETag will be last release date.
			return (ETag)DateTime.UtcNow.RoundUp(TimeSpan.FromHours(2)).ToIsoString();
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
				var fredSymbol = Symbol.Code.GetFredSymbol();

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
					return new SymbolHistoryRecord(x.Date, v, v, v, v, 0);
				}).ToArray(), newContinuationToken != default);
			}
		}
	}
}