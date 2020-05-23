namespace SomeDataProvider.DataStorage.HistoryStores
{
	using System;
	using System.Threading;
	using System.Threading.Tasks;

	using SomeDataProvider.DataStorage.Definitions;

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

		public Task<GetSymbolHistoryResult> GetSymbolHistoryAsync(ISymbol symbol, HistoryInterval historyInterval, DateTime start, DateTime end, int limit, string? continuationToken, CancellationToken cancellationToken = default)
		{
			throw new NotImplementedException();
		}

		public void Dispose()
		{
			if (_disposeFredService)
				_fredService.Dispose();
		}
	}
}