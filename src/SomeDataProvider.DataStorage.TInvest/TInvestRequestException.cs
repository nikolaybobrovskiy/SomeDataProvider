namespace SomeDataProvider.DataStorage.TInvest;

public enum TInvestFailureKind
{
	Source,
	Limit,
	Unsupported,

	/// <summary>The operator must fix something (token, access level, environment, configuration); retrying cannot help.</summary>
	Configuration,

	/// <summary>The source is not ready yet: its startup check is still being retried in the background.</summary>
	Starting,
}

public sealed class TInvestRequestException : Exception
{
	public TInvestRequestException(string safeMessage, TInvestFailureKind kind = TInvestFailureKind.Source, int? retryAfterSeconds = null)
		: base(safeMessage)
	{
		if (retryAfterSeconds is < 0 or > 3600) throw new ArgumentOutOfRangeException(nameof(retryAfterSeconds));
		Kind = kind;
		RetryAfterSeconds = retryAfterSeconds;
	}

	public TInvestFailureKind Kind { get; }
	public int? RetryAfterSeconds { get; }
}
