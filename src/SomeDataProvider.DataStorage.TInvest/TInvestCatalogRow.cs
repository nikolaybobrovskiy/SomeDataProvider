namespace SomeDataProvider.DataStorage.TInvest;

public sealed record TInvestCatalogRow(string NodeID, string Name, string? ParentNodeID, TInvestInstrument? Instrument = null, int Kind = 0);
