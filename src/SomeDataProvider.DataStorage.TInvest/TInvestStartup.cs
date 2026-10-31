namespace SomeDataProvider.DataStorage.TInvest;

public static class TInvestStartup
{
	public static async Task CheckAsync(ITInvestGateway gateway, TInvestOptions options, DateTimeOffset deadline, CancellationToken cancellationToken = default)
	{
		var access = await gateway.CheckAccessAsync(deadline, cancellationToken).ConfigureAwait(false);
		if (!access.Authenticated) throw new TInvestRequestException("T-Invest token is missing, expired or for the other environment.");
		if (!options.IsSandbox && (access.ReadOnlyAccounts.Count == 0 || access.ReadOnlyAccounts.Any(readOnly => !readOnly)))
			throw new TInvestRequestException("A read-only T-Invest token is required for production data.");
	}
}
