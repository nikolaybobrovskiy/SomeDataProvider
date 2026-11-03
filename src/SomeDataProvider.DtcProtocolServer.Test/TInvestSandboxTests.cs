namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Protocol;
using Dtc.Protocol.Messages;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;

[TestFixture]
public partial class TInvestSandboxTests
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
		{
			Assert.Ignore("The sandbox token, symbol and exchange variables must be set explicitly.");
		}

		var options = new TInvestOptions
		{
			Environment = "Sandbox"
		};
		Assert.That(options.IsSandbox, Is.True);
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
		using var sdk = new SdkTInvestGateway(new TInvestToken(token!), options, new TInvestRateLimiter());
		using var bounded = new BoundedGateway(sdk);
		var stage = "access";
		var elapsed = Stopwatch.StartNew();
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
			var rows = await client.SearchSymbolsAsync(new SymbolSearchRequest
			{
				SearchText = symbol!,
				Exchange = exchange!,
				SearchType = SearchTypeEnum.SEARCH_TYPE_BY_SYMBOL
			}, deadline.Token);
			Assert.That(rows.Length > 0 && rows.Length <= 20 && rows.All(row => row.MinPriceIncrement > 0), Is.True, "Search has complete bounded rows.");
			TestContext.Out.WriteLine("sandbox=true definition_complete=true search_complete=true rows=" + rows.Length + " reserved_upstream_attempts=" + bounded.Calls + " upstream_cap=30");
		}
		catch (Exception ex)
		{
			// NUnit must not render SDK payloads, credentials, tickers or market values in failure output.
			Assert.Fail("Read-only sandbox structural check failed; stage=" + stage + "; exception=" + ex.GetType().Name + "; grpc_status=" + (sdk.LastFailureStatus ?? "none") + "; api_code=" + (sdk.LastFailureCode ?? "none") + "; bridge_kind=" + (ex is TInvestRequestException bridge ? bridge.Kind.ToString() : "none") + "; elapsed_ms=" + elapsed.ElapsedMilliseconds + "; reserved_attempts=" + bounded.Calls + "; no live payload was recorded.");
		}
	}

	[Test]
	[Explicit("Local read-only sandbox candle check; never enabled by a default or CI run.")]
	[Category("TInvestSandbox")]
	public async Task ReadsOneMinuteDayThroughBridgeHistoryReader()
	{
		var token = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_TOKEN");
		var ticker = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_SYMBOL");
		var exchange = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_EXCHANGE");
		if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(ticker) || string.IsNullOrEmpty(exchange))
		{
			Assert.Ignore("The sandbox token, symbol and exchange variables must be set explicitly.");
		}

		var options = new TInvestOptions
		{
			Environment = "Sandbox"
		};
		Assert.That(options.Environment, Is.EqualTo("Sandbox"));
		using var sdk = new SdkTInvestGateway(new TInvestToken(token!), options, new TInvestRateLimiter());
		using var bounded = new BoundedGateway(sdk);
		using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		var elapsed = Stopwatch.StartNew();
		var stage = "access";
		try
		{
			await TInvestStartup.CheckAsync(bounded, options, DateTimeOffset.UtcNow.AddSeconds(8), stop.Token);
			using var store = new TInvestStore(bounded, options);
			stage = "definition";
			var symbol = await store.ResolveAsync(ticker!, exchange!, DateTimeOffset.UtcNow.AddSeconds(8), stop.Token);
			Assert.That(symbol != null, Is.True, "A complete definition is available.");
			var day = DateTimeOffset.UtcNow.Date.AddDays(-1);
			while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
			{
				day = day.AddDays(-1);
			}

			stage = "candles";
			using var reader = await store.CreateCandleReaderAsync(symbol!, 60, day, day.AddDays(1), DateTimeOffset.UtcNow.AddSeconds(20), stop.Token);
			var records = new List<SomeDataProvider.DataStorage.Definitions.ISymbolHistoryRecord>();
			while (true)
			{
				var page = await reader.ReadSymbolHistoryAsync(stop.Token);
				records.AddRange(page.Records);
				if (!page.HasMore)
				{
					break;
				}
			}

			Assert.That(records.Count > 0 && records.Count <= 1440, Is.True, "One minute day has a bounded non-empty answer.");
			Assert.That(records.All(r => r.TimeStamp >= day && r.TimeStamp < day.AddDays(1)) && records.Select(r => r.TimeStamp).SequenceEqual(records.Select(r => r.TimeStamp).Order()), Is.True, "Half-open ordered day.");
			TestContext.Out.WriteLine("sandbox=true interval_seconds=60 records=" + records.Count + " first_second_in_day=" + (records[0].TimeStamp - day).TotalSeconds + " last_second_in_day=" + (records[^1].TimeStamp - day).TotalSeconds + " ordered=true half_open=true reserved_attempts=" + bounded.Calls + " upstream_cap=30");
		}
		catch (Exception ex)
		{
			Assert.Fail("Sandbox candle structural check failed; stage=" + stage + "; exception=" + ex.GetType().Name + "; grpc_status=" + (sdk.LastFailureStatus ?? "none") + "; api_code=" + (sdk.LastFailureCode ?? "none") + "; bridge_kind=" + (ex is TInvestRequestException bridge ? bridge.Kind.ToString() : "none") + "; elapsed_ms=" + elapsed.ElapsedMilliseconds + "; reserved_attempts=" + bounded.Calls);
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

		public async Task<ITInvestMarketDataStream> OpenMarketDataStreamAsync(CancellationToken cancellationToken)
		{
			Admit(2);
			return new BoundedStream(await inner.OpenMarketDataStreamAsync(cancellationToken), this);
		}

		public Task<TInvestMarketSnapshot> GetMarketSnapshotAsync(string uid, bool includeDealer, DateTimeOffset deadline, CancellationToken cancellationToken)
		{
			Admit(6);
			return inner.GetMarketSnapshotAsync(uid, includeDealer, deadline, cancellationToken);
		}

		public Task<TInvestOrderBook> GetOrderBookAsync(string uid, int depth, DateTimeOffset deadline, CancellationToken cancellationToken)
		{
			Admit(6);
			return inner.GetOrderBookAsync(uid, depth, deadline, cancellationToken);
		}

		public void Dispose() => inner.Dispose();
		public Task<TInvestRateLimiter.Reservation> ReserveCandlesAsync(int calls, DateTimeOffset deadline, CancellationToken cancellationToken) => inner.ReserveCandlesAsync(calls, deadline, cancellationToken);
		public Task<System.Collections.Generic.IReadOnlyList<TInvestCandle>> GetCandlesAsync(TInvestCandleRequest request, TInvestRateLimiter.Reservation reservation, DateTimeOffset deadline, CancellationToken cancellationToken)
		{
			Admit(2);
			return inner.GetCandlesAsync(request, reservation, deadline, cancellationToken);
		}

		// The sandbox has no trade archives, and the opt-in checks never call the archive host.
		public Task<TInvestRateLimiter.Reservation> ReserveArchivesAsync(int downloads, DateTimeOffset deadline, CancellationToken cancellationToken) =>
			throw new TInvestRequestException("Archive downloads are not part of the sandbox checks.");

		public Task<bool> DownloadTradeArchiveAsync(string instrumentId, DateOnly day, string destination, TInvestRateLimiter.Reservation? reservation, DateTimeOffset deadline, CancellationToken cancellationToken) =>
			throw new TInvestRequestException("Archive downloads are not part of the sandbox checks.");

		public Task<System.Collections.Generic.IReadOnlyList<TInvestTrade>> GetLastTradesAsync(string uid, DateTimeOffset from, DateTimeOffset to, TInvestRateLimiter.Reservation reservation, DateTimeOffset deadline, CancellationToken cancellationToken)
		{
			Admit(2);
			return inner.GetLastTradesAsync(uid, from, to, reservation, deadline, cancellationToken);
		}

		void Admit(int maximumAttempts)
		{
			// Access/find: at most 2 attempts; definition plus option/underlying: at most 6.
			// Reserve worst-case attempts, including the bridge's one RESOURCE_EXHAUSTED retry.
			if (Interlocked.Add(ref _calls, maximumAttempts) > 30)
			{
				throw new TInvestRequestException("Sandbox request budget exhausted.");
			}
		}

		sealed class BoundedStream(ITInvestMarketDataStream innerStream, BoundedGateway budget) : ITInvestMarketDataStream
		{
			public Task<IReadOnlyList<string>> SendAsync(TInvestStreamRequest request, DateTimeOffset deadline, CancellationToken cancellationToken)
			{
				budget.Admit(1);
				return innerStream.SendAsync(request, deadline, cancellationToken);
			}

			public IAsyncEnumerable<TInvestMarketEvent> ReadAllAsync(CancellationToken cancellationToken) => innerStream.ReadAllAsync(cancellationToken);
			public void Dispose() => innerStream.Dispose();
		}
	}
}
