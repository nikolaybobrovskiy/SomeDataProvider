namespace SomeDataProvider.DataStorage.HistoryStores
{
	using System;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;

	using NBLib.BuiltInTypes;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DataStorage.InMem;

	public class SymbolHistoryFredStore : ISymbolHistoryStore, IDisposable
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

		public async Task<GetSymbolHistoryResult> GetSymbolHistoryAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, string? continuationToken, CancellationToken cancellationToken = default)
		{
			var fredSymbol = symbol.Code.GetFredSymbol();

			var offset = 0;
			if (!continuationToken.IsEmpty())
				offset = int.Parse(continuationToken);

			var observationsData = await _fredService.GetObservationsAsync(fredSymbol.SeriesId, start: start, end: end, transformation: fredSymbol.Units, offset: offset, limit: limit, cancellationToken: cancellationToken);
			if (observationsData == null) return GetSymbolHistoryResult.Empty;

			return new GetSymbolHistoryResult(observationsData.Observations.Where(x => x.Value != null).Select(x =>
			{
				var v = x.Value!.Value;
				return new SymbolHistoryRecord(x.Date, v, v, v, v, 0);
			}).ToArray(), observationsData.Observations.Length < limit ? null : (offset + limit).ToString());
		}

		public void Dispose()
		{
			if (_disposeFredService)
				_fredService.Dispose();
		}
	}
}