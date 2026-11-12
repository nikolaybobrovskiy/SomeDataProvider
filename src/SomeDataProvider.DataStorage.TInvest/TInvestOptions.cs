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
	/// <summary>Exchange or IncludeWeekend. This selects candles only, never dealer-trade inclusion.</summary>
	public string CandleSource { get; init; } = "Exchange";
	public int MaxStreams { get; init; } = 4;
	/// <summary>Maximum upstream and downstream order-book levels per side: 1, 10, 20, 30, 40 or 50.</summary>
	public int MaxDepth { get; init; } = 50;
	public TimeSpan StreamSilenceTimeout { get; init; } = TimeSpan.FromSeconds(180);
	public int SessionQueueLimit { get; init; } = 50_000;
	/// <summary>Recent reconciliation requests and clock refreshes per rolling 60 seconds per stable profile: 1-12.</summary>
	public int LiveReconcileRequestsPerMinute { get; init; } = 6;
	/// <summary>Tick history: 1-31 UTC days per DTC request.</summary>
	public int MaxTickDaysPerRequest { get; init; } = 31;
	/// <summary>Tick history: 1,000-5,000,000 ticks per DTC request.</summary>
	public int MaxTicksPerRequest { get; init; } = 5_000_000;
	/// <summary>Full path of the downloaded trade archive folder; null selects the default under the local application data folder.</summary>
	public string? ArchiveCacheFolder { get; init; }
	/// <summary>Trade archive cache size cap in bytes: 100 MB to 1 TB (default 20 GB).</summary>
	public long ArchiveCacheMaxBytes { get; init; } = 20L * 1024 * 1024 * 1024;
	public string[] RecordSymbols { get; init; } = Array.Empty<string>();
	public bool Enabled => TokenSource.Length != 0;
	public bool IsSandbox => Environment == "Sandbox";
	public string EffectiveArchiveCacheFolder => ArchiveCacheFolder ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SomeDataProvider", "tinvest-archives");
	public string EffectiveCredentialTarget => CredentialTarget ?? $"SomeDataProvider/TInvest/{Environment}";

	public void Validate()
	{
		if (LiveReconcileRequestsPerMinute is < 1 or > 12)
			throw new ArgumentException("TInvest:LiveReconcileRequestsPerMinute must be 1-12.");

		if (CandleSource is not ("Exchange" or "IncludeWeekend"))
			throw new ArgumentException("TInvest:CandleSource must be Exchange or IncludeWeekend; dealer inclusion is a separate setting.");

		if (MaxStreams is < 1 or > 32)
		{
			throw new ArgumentException("TInvest:MaxStreams must be 1-32.");
		}

		if (MaxDepth is not (1 or 10 or 20 or 30 or 40 or 50))
		{
			throw new ArgumentException("TInvest:MaxDepth must be 1, 10, 20, 30, 40 or 50.");
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

		if (MaxTickDaysPerRequest is < 1 or > 31)
		{
			throw new ArgumentException("TInvest:MaxTickDaysPerRequest must be 1-31.");
		}

		if (MaxTicksPerRequest is < 1_000 or > 5_000_000)
		{
			throw new ArgumentException("TInvest:MaxTicksPerRequest must be 1000-5000000.");
		}

		if (ArchiveCacheFolder != null && (string.IsNullOrWhiteSpace(ArchiveCacheFolder) || ArchiveCacheFolder.Length > 240 || ArchiveCacheFolder.Any(char.IsControl) || ArchiveCacheFolder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(ArchiveCacheFolder) || HasInvalidFolderName(ArchiveCacheFolder)))
		{
			throw new ArgumentException("TInvest:ArchiveCacheFolder must be a full path of 1-240 valid characters, or omitted for the default folder.");
		}

		if (ArchiveCacheMaxBytes is < 100L * 1024 * 1024 or > 1024L * 1024 * 1024 * 1024)
		{
			throw new ArgumentException("TInvest:ArchiveCacheMaxBytes must be 104857600 (100 MB) to 1099511627776 (1 TB).");
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

	// Path.GetInvalidPathChars() misses the characters that are only invalid inside a name: wildcards, quotes and a colon after the drive.
	static bool HasInvalidFolderName(string folder)
	{
		var root = Path.GetPathRoot(folder) ?? string.Empty;
		// A drive root has its one colon; the server and share of a UNC root are names like any other.
		var unc = root.StartsWith("\\\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal);
		var checkedText = (unc ? root[2..] : string.Empty) + folder[root.Length..];
		return checkedText.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0;
	}

	static bool ValidRecordSymbol(string? value) => value != null && value.Length is >= 3 and <= 128 && !value.StartsWith("fred", StringComparison.OrdinalIgnoreCase) && value.LastIndexOf('-') > 0 && value[^1] != '-' && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
