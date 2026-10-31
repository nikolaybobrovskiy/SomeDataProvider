namespace SomeDataProvider.DataStorage.TInvest;

public sealed class TInvestToken
{
	readonly string _value;

	public TInvestToken(string value)
	{
		if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("T-Invest token is missing.");
		_value = value;
	}

	internal string Value => _value;

	public string Redact(string text) => text.Replace(_value, "<redacted>", StringComparison.Ordinal);

	public override string ToString() => "<redacted>";
}
