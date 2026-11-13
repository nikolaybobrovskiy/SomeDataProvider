namespace SomeDataProvider.DataStorage.TInvest;

public sealed class TInvestCatalogException(string reason) : Exception("The catalog request could not be served.")
{
	public string Reason { get; } = reason;
}
