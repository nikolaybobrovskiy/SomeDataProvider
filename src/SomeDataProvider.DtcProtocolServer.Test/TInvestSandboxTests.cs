namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Protocol;
using Dtc.Protocol.Messages;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;

[TestFixture]
public class TInvestSandboxTests
{
	[Test]
	[Explicit("Local read-only sandbox check; never enabled by a default or CI run.")]
	[Category("TInvestSandbox")]
	public async Task ResolvesDefinitionAndSearchThroughBridge()
	{
		var token = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_TOKEN");
		var symbol = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_SYMBOL");
		var exchange = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_EXCHANGE");
		if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(exchange))
			Assert.Ignore("The sandbox token, symbol and exchange variables must be set explicitly.");
		var options = new TInvestOptions { Environment = "Sandbox" };
		Assert.That(options.IsSandbox, Is.True);
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
		using var sdk = new SdkTInvestGateway(new TInvestToken(token!), options, new TInvestRateLimiter());
		using var bounded = new BoundedGateway(sdk);
		var stage = "access";
		try
		{
			await TInvestStartup.CheckAsync(bounded, options, DateTimeOffset.UtcNow.AddSeconds(8), deadline.Token);
			stage = "definition";
			using var host = new TInvestTestHost(bounded);
			await using var client = host.CreateClient();
			await client.ConnectAsync(deadline.Token);
			var definition = await client.GetSecurityDefinitionAsync(symbol!, exchange!, deadline.Token);
			// Assert boolean structure, never a value from the live response.
			Assert.That(definition != null && definition.MinPriceIncrement > 0 && definition.Currency.Length == 3, Is.True, "Definition fields are complete.");
			stage = "search";
			var rows = await client.SearchSymbolsAsync(new SymbolSearchRequest { SearchText = symbol!, Exchange = exchange!, SearchType = SearchTypeEnum.SEARCH_TYPE_BY_SYMBOL }, deadline.Token);
			Assert.That(rows.Length > 0 && rows.Length <= 20 && rows.All(row => row.MinPriceIncrement > 0), Is.True, "Search has complete bounded rows.");
			TestContext.Out.WriteLine("sandbox=true definition_complete=true search_complete=true rows=" + rows.Length + " reserved_upstream_attempts=" + bounded.Calls + " upstream_cap=30");
		}
		catch (Exception ex)
		{
			// NUnit must not render SDK payloads, credentials, tickers or market values in failure output.
			Assert.Fail("Read-only sandbox structural check failed; stage=" + stage + "; status=" + (sdk.LastFailureStatus ?? ex.GetType().Name) + "; reserved_attempts=" + bounded.Calls + "; no live payload was recorded.");
		}
	}

	sealed class BoundedGateway(ITInvestGateway inner) : ITInvestGateway
	{
		int _calls;

		public int Calls => _calls;

		public Task<TInvestAccess> CheckAccessAsync(DateTimeOffset deadline, CancellationToken cancellationToken)
		{
			Admit(2);
			return inner.CheckAccessAsync(deadline, cancellationToken);
		}

		public Task<TInvestInstrument?> GetInstrumentAsync(string ticker, string classCode, DateTimeOffset deadline, CancellationToken cancellationToken)
		{
			Admit(6);
			return inner.GetInstrumentAsync(ticker, classCode, deadline, cancellationToken);
		}

		public Task<System.Collections.Generic.IReadOnlyList<TInvestInstrumentShort>> FindInstrumentsAsync(string query, DateTimeOffset deadline, CancellationToken cancellationToken)
		{
			Admit(2);
			return inner.FindInstrumentsAsync(query, deadline, cancellationToken);
		}

		public void Dispose() => inner.Dispose();

		void Admit(int maximumAttempts)
		{
			// Access/find: at most 2 attempts; definition plus option/underlying: at most 6.
			// Reserve worst-case attempts, including the bridge's one RESOURCE_EXHAUSTED retry.
			if (Interlocked.Add(ref _calls, maximumAttempts) > 30)
				throw new TInvestRequestException("Sandbox request budget exhausted.");
		}
	}
}
