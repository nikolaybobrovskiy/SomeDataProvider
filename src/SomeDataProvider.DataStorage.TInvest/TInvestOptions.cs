namespace SomeDataProvider.DataStorage.TInvest;

/// <summary>Stage 2 settings. Tokens deliberately are not part of this object.</summary>
public sealed class TInvestOptions
{
	public string TokenSource { get; init; } = string.Empty;

	public string Environment { get; init; } = "Production";

	public string? CredentialTarget { get; init; }

	public bool SearchOnlyApiTradeable { get; init; } = true;

	public bool Enabled => TokenSource.Length != 0;

	public bool IsSandbox => Environment == "Sandbox";

	public string EffectiveCredentialTarget => CredentialTarget ?? $"SomeDataProvider/TInvest/{Environment}";

	public void Validate()
	{
		if (TokenSource is not ("" or "CredentialManager" or "Environment" or "File"))
			throw new ArgumentException("TInvest:TokenSource must be empty, CredentialManager, Environment or File.");
		if (Environment is not ("Production" or "Sandbox"))
			throw new ArgumentException("TInvest:Environment must be Production or Sandbox.");
		if (CredentialTarget != null && (string.IsNullOrWhiteSpace(CredentialTarget) || CredentialTarget.Length > 256 || CredentialTarget.Any(char.IsControl)))
			throw new ArgumentException("TInvest:CredentialTarget must be 1-256 non-control characters, not whitespace, or omitted.");
	}
}
