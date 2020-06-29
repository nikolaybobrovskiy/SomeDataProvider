namespace SomeDataProvider.DataStorage.HistoryStores
{
	using System;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;

	using NBLib.DateTime;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DataStorage.InMem;

	public class SymbolHistoryFredStore : ICacheableSymbolHistoryStore, IDisposable
	{
		readonly Fred.Service _fredService;
		readonly bool _disposeFredService;

		public SymbolHistoryFredStore(Fred.ServiceApiKey fredServiceApiKey)
		{
			_fredService = new Fred.Service(fredServiceApiKey);
			_disposeFredService = true;
		}

		public SymbolHistoryFredStore(Fred.Service fredService)
		{
			_fredService = fredService;
		}

		public async Task<SymbolHistoryResponse> GetSymbolHistoryAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, ContinuationToken? continuationToken, CancellationToken cancellationToken = default)
		{
			var fredSymbol = symbol.Code.GetFredSymbol();

			var offset = 0;
			if (continuationToken != null)
				offset = int.Parse(continuationToken);

			var observationsData = await _fredService.GetObservationsAsync(fredSymbol.SeriesId, start: start, end: end, transformation: fredSymbol.Units, offset: offset, limit: limit, cancellationToken: cancellationToken);
			if (observationsData == null) return SymbolHistoryResponse.Empty;

			var newContinuationToken = observationsData.Observations.Length < limit ? default : (ContinuationToken)(offset + limit).ToString();
			return new SymbolHistoryResponse(observationsData.Observations.Where(x => x.Value != null).Select(x =>
			{
				var v = x.Value!.Value;
				// TODO: RevisablePeriod = two last values of the most available in service.
				return new SymbolHistoryRecord(x.Date, v, v, v, v, 0);
			}).ToArray(), newContinuationToken);
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
			return (ETag)DateTime.UtcNow.RoundUp(TimeSpan.FromHours(12)).ToIsoString();
		}
	}
}