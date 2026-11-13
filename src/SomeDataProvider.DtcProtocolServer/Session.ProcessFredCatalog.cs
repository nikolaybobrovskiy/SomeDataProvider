namespace SomeDataProvider.DtcProtocolServer;

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Protocol.Private;
using SomeDataProvider.DataStorage.Fred;
using SomeDataProvider.DtcProtocolServer.Data;

partial class Session
{
	async Task ProcessFredCatalogAsync(SymbolCatalogRequest request, CancellationToken ct)
	{
		try
		{
			var catalog = ((StoresProvider)_symbolsStoreProvider).GetFredCatalog();
			var page = await catalog.ReadAsync(request, ct).ConfigureAwait(false);
			var frames = page.Select(EncodeProviderMessage).ToArray();
			ct.ThrowIfCancellationRequested();
			foreach (var frame in frames) SendResponse(frame);
		}
		catch (FredCatalogException ex)
		{
			SendProviderReject(request.RequestID, request.Type, ex.Reason, "The FRED category listing could not be read. Refresh or retry later.");
		}
	}
}
