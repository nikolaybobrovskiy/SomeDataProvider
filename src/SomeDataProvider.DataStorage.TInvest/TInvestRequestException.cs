namespace SomeDataProvider.DataStorage.TInvest;

public enum TInvestFailureKind
{
	Source,
	Limit,
	Unsupported,
}

public sealed class TInvestRequestException : Exception
{
	public TInvestRequestException(string safeMessage, TInvestFailureKind kind = TInvestFailureKind.Source)
		: base(safeMessage)
	{
		Kind = kind;
	}

	public TInvestFailureKind Kind { get; }
}
