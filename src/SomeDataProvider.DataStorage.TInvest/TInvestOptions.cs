namespace SomeDataProvider.DataStorage.TInvest;
/// <summary>Stage 2 settings. Tokens deliberately are not part of this object.</summary>
public sealed class TInvestOptions
{
	public string TokenSource { get; init; } = string.Empty;
	public string Environment { get; init; } = "Production";
	public string? CredentialTarget { get; init; }
	public bool SearchOnlyApiTradeable { get; init; } = true;
	/// <summary>1-400 upstream candle calls per DTC request, including planned pages.</summary>
	    public int MaxCandleCallsPerRequest { get; init; } = 400;
	public bool IncludeDealerTrades { get; init; }
	public int MaxStreams { get; init; } = 4;
	public TimeSpan StreamSilenceTimeout { get; init; } = TimeSpan.FromSeconds(180);
	public int SessionQueueLimit { get; init; } = 50_000;
	public string[] RecordSymbols { get; init; } = Array.Empty<string>();
	public bool Enabled => TokenSource.Length != 0;
	public bool IsSandbox => Environment == "Sandbox";
	public string EffectiveCredentialTarget => CredentialTarget ?? $"SomeDataProvider/TInvest/{Environment}";

	public void Validate()
	{
		if (MaxStreams is < 1 or > 32)
		{
			throw new ArgumentException("TInvest:MaxStreams must be 1-32.");
		}

		if (StreamSilenceTimeout < TimeSpan.FromSeconds(30) || StreamSilenceTimeout > TimeSpan.FromSeconds(900))
		{
			throw new ArgumentException("TInvest:StreamSilenceTimeout must be 00:00:30-00:15:00.");
		}

		if (SessionQueueLimit is < 128 or > 100_000)
		{
			throw new ArgumentException("TInvest:SessionQueueLimit must be 128-100000.");
		}

		if (RecordSymbols == null || RecordSymbols.Length > 150 * MaxStreams || RecordSymbols.Any(s => !ValidRecordSymbol(s)) || RecordSymbols.Distinct(StringComparer.Ordinal).Count() != RecordSymbols.Length)
		{
			throw new ArgumentException("TInvest:RecordSymbols must contain unique TICKER-CLASSCODE entries, 3-128 ASCII letters/digits/._- each; at most 150 per MaxStreams, and no FRED symbols.");
		}

		if (MaxCandleCallsPerRequest is < 1 or > 400)
		{
			throw new ArgumentException("TInvest:MaxCandleCallsPerRequest must be 1-400.");
		}

		if (TokenSource is not ("" or "CredentialManager" or "Environment" or "File"))
		{
			throw new ArgumentException("TInvest:TokenSource must be empty, CredentialManager, Environment or File.");
		}

		if (Environment is not ("Production" or "Sandbox"))
		{
			throw new ArgumentException("TInvest:Environment must be Production or Sandbox.");
		}

		if (CredentialTarget != null && (string.IsNullOrWhiteSpace(CredentialTarget) || CredentialTarget.Length > 256 || CredentialTarget.Any(char.IsControl)))
		{
			throw new ArgumentException("TInvest:CredentialTarget must be 1-256 non-control characters, not whitespace, or omitted.");
		}
	}

	static bool ValidRecordSymbol(string? value) => value != null && value.Length is >= 3 and <= 128 && !value.StartsWith("fred", StringComparison.OrdinalIgnoreCase) && value.LastIndexOf('-') > 0 && value[^1] != '-' && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
