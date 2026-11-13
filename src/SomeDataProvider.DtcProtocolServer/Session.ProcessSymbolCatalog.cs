namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Protocol.Private;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DtcProtocolServer.Data;

partial class Session
{
	async Task ProcessSymbolCatalogAsync(SymbolCatalogRequest request, CancellationToken ct)
	{
		var store = (_symbolsStoreProvider as StoresProvider)?.GetTInvestInformationStore();
		if (request.Catalog != "t-invest" || store?.SupportsCatalog != true ||
			_providerNegotiation == null || (_providerNegotiation.Features & ProviderInformationFeatures.SymbolCatalogV1) == 0)
		{
			SendProviderReject(request.RequestID, request.Type, "FeatureUnavailable", "This catalog is unavailable.");
			return;
		}
		try
		{
			var snapshot = await store.GetCatalogAsync(ct).ConfigureAwait(false);
			if (request.SnapshotVersion != null && request.SnapshotVersion != snapshot.Version) throw new TInvestCatalogException("SnapshotExpired");
			var offset = store.ReadCatalogCursor(snapshot, request.NodeID, request.Filter, request.PageSize, request.Cursor);
			var rows = snapshot.GetRows(request.NodeID, request.Filter);
			if (offset > rows.Count) throw new TInvestCatalogException("InvalidCursor");
			var count = Math.Min(request.PageSize, rows.Count - offset);
			var messages = new List<ProviderInformationMessage>();
			foreach (var row in rows.Skip(offset).Take(count))
			{
				var index = messages.Count;
				if (row.Instrument is not { } instrument)
				{
					messages.Add(new SymbolCatalogNode
					{
						RequestID = request.RequestID, Catalog = request.Catalog, NodeID = row.NodeID,
						Name = row.Name, ParentNodeID = row.ParentNodeID, RowIndex = index, RowsInPage = count, SnapshotVersion = snapshot.Version,
					});
					continue;
				}
				var symbol = instrument.Short.Ticker + "-" + instrument.Short.ClassCode;
				var context = TInvestStore.Complete(instrument) ? MakeInstrumentContext(instrument, store.Options, symbol, snapshot.Created) : null;
				messages.Add(new SymbolCatalogEntry
				{
					RequestID = request.RequestID, Catalog = request.Catalog, NodeID = row.NodeID,
					Symbol = symbol, Name = row.Name, Kind = row.Kind, InstrumentUID = instrument.Short.Uid, Board = instrument.Board,
					Currency = string.IsNullOrWhiteSpace(instrument.Currency) ? null : instrument.Currency.ToUpperInvariant(),
					Features = context?.Features, UIDEpoch = context?.UIDEpoch, RowIndex = index, RowsInPage = count, SnapshotVersion = snapshot.Version,
				});
			}
			messages.Add(new SymbolCatalogEnd
			{
				RequestID = request.RequestID, Catalog = request.Catalog, NodeID = request.NodeID,
				RowCount = count, SnapshotVersion = snapshot.Version, Total = rows.Count, Consistency = 1,
				NextCursor = offset + count < rows.Count ? store.MakeCatalogCursor(snapshot, request.NodeID, request.Filter, request.PageSize, offset + count) : null,
			});
			// Encode the entire bounded page before sending its first row.
			var frames = messages.Select(EncodeProviderMessage).ToArray();
			ct.ThrowIfCancellationRequested();
			foreach (var frame in frames) SendResponse(frame);
		}
		catch (TInvestCatalogException ex)
		{
			SendProviderReject(request.RequestID, request.Type, ex.Reason, "The catalog request could not be served. Refresh the catalog or retry later.");
		}
	}
}
