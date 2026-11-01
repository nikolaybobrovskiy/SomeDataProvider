namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Client;
using Dtc.Protocol;
using Dtc.Protocol.Messages;
using Dtc.Protocol.Vls;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Data;

[TestFixture]
[NonParallelizable]
public class TInvestWireTests
{
	[TestCase(TInvestInstrumentKind.Share, SecurityTypeEnum.SECURITY_TYPE_STOCK)]
	[TestCase(TInvestInstrumentKind.Etf, SecurityTypeEnum.SECURITY_TYPE_STOCK)]
	[TestCase(TInvestInstrumentKind.Bond, SecurityTypeEnum.SECURITY_TYPE_BOND)]
	[TestCase(TInvestInstrumentKind.Future, SecurityTypeEnum.SECURITY_TYPE_FUTURES)]
	[TestCase(TInvestInstrumentKind.Currency, SecurityTypeEnum.SECURITY_TYPE_FOREX)]
	[TestCase(TInvestInstrumentKind.StockOption, SecurityTypeEnum.SECURITY_TYPE_STOCK_OPTION)]
	[TestCase(TInvestInstrumentKind.FuturesOption, SecurityTypeEnum.SECURITY_TYPE_FUTURES_OPTION)]
	[TestCase(TInvestInstrumentKind.IndexOption, SecurityTypeEnum.SECURITY_TYPE_INDEX_OPTION)]
	public async Task DefinitionsRoundTripEveryKindThroughRealClient(TInvestInstrumentKind kind, SecurityTypeEnum expected)
	{
		using var gateway = new ScriptedTInvestGateway();
		var expiry = new DateTimeOffset(2028, 6, 15, 12, 0, 0, TimeSpan.Zero);
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent(kind: kind) with { Expiration = expiry });
		using var host = new TInvestTestHost(gateway);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		var definition = (await client.GetSecurityDefinitionAsync("SYNTH", "TESTCC"))!;
		Assert.That(definition.Symbol, Is.EqualTo("SYNTH"));
		Assert.That(definition.Exchange, Is.EqualTo("TESTCC"));
		Assert.That(definition.SecurityType, Is.EqualTo(expected));
		Assert.That(definition.MinPriceIncrement, Is.EqualTo((double)(float)0.01));
		Assert.That(definition.Currency, Is.EqualTo("ZZZ"));
		Assert.That(definition.Description, Does.Contain("lot 10"));
		Assert.That(definition.ExchangeSymbol, Is.EqualTo("SYNTH_TESTCC"));
		Assert.That(definition.PriceDisplayFormat, Is.EqualTo(PriceDisplayFormatEnum.PRICE_DISPLAY_FORMAT_DECIMAL_2));
		Assert.That(definition.SecurityExpirationDate, Is.EqualTo(DtcTimestamp.FromDateTimeOffset(expiry)));
		Assert.That(definition.HasMarketDepthData, Is.False);
	}

	[TestCase("SYNTH", "TESTCC")]
	[TestCase("SYNTH-TESTCC", "")]
	public async Task ExactDefinitionEchoesRequestedFormAndLogonOnlyAdvertisesThisStage(string symbol, string exchange)
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.Add(ScriptedTInvestGateway.Invent());
		using var host = new TInvestTestHost(gateway);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		var definition = (await client.GetSecurityDefinitionAsync(symbol, exchange))!;
		Assert.That(definition.Symbol, Is.EqualTo(symbol));
		Assert.That(definition.Exchange, Is.EqualTo(exchange));
		Assert.That(client.LogonResponse!.ResultText, Does.Contain("FRED, T-Invest (sandbox)"));
		Assert.That(client.LogonResponse.SymbolExchangeDelimiter, Is.Empty);
		Assert.That(client.Capabilities!.MarketDepthIsSupported, Is.False);
		Assert.That(client.Capabilities.ResubscribeWhenMarketDataFeedAvailable, Is.True);
		Assert.That(client.Capabilities.TradingIsSupported, Is.False);
	}

	[Test]
	public async Task SearchThenExactUsesCompleteClientCacheAndLiveSubscriptionCompletes()
	{
		using var gateway = new ScriptedTInvestGateway();
		gateway.Instruments.AddRange(Enumerable.Range(0, 50).Select(i => ScriptedTInvestGateway.Invent("SYNTH" + i)));
		using var host = new TInvestTestHost(gateway);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		var rows = await client.SearchSymbolsAsync(new SymbolSearchRequest { SearchText = "SYNTH", Exchange = "TESTCC", SearchType = SearchTypeEnum.SEARCH_TYPE_BY_SYMBOL });
		Assert.That(rows.Length, Is.EqualTo(20));
		Assert.That(rows.All(row => row.MinPriceIncrement > 0 && row.Currency == "ZZZ"), Is.True);
		var calls = gateway.Lookups.Count;
		var exact = (await client.GetSecurityDefinitionAsync(rows[0].Symbol, rows[0].Exchange))!;
		Assert.That(exact.MinPriceIncrement, Is.GreaterThan(0));
		Assert.That(gateway.Lookups.Count, Is.EqualTo(calls));
		var subscription = await client.SubscribeMarketDataAsync(rows[0].Symbol, rows[0].Exchange);
		Assert.That(subscription, Is.GreaterThan(0));
	}

	[TestCase(102, 103)]
	[TestCase(120, 121)]
	public void MarketDataAndDepthGetCleanWireRejects(int requestType, int rejectType)
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway);
		using var client = new TcpClient();
		client.Connect(IPAddress.Loopback, host.Port);
		host.WaitUntilConnected();
		client.ReceiveTimeout = 10000;
		var request = new byte[256];
		var length = requestType == 102
			? new MarketDataRequest { SymbolID = 7, Symbol = "UNKNOWN", Exchange = "TESTCC", RequestAction = RequestActionEnum.SUBSCRIBE }.ToVls(request, Encoding.UTF8)
			: new MarketDepthRequest { SymbolID = 7, Symbol = "SYNTH-TESTCC", RequestAction = RequestActionEnum.SUBSCRIBE, NumLevels = 10 }.ToVls(request, Encoding.UTF8);
		client.GetStream().Write(request.AsSpan(0, length));
		var response = ReadFrame(client);
		Assert.That(DtcWire.TypeOf(response), Is.EqualTo(rejectType));
		if (requestType == 102)
		{
			DtcVlsDecoder.Decode(response, out Dtc.Protocol.Vls.s_MarketDataReject reject);
			var text = DtcVlsDecoder.GetString(response, reject.RejectText, Encoding.UTF8);
			Assert.That(text, Does.Contain("UNKNOWN"));
			Assert.That(text.Any(char.IsControl), Is.False);
		}
	}

	[Test]
	public void HistoryGetsCleanStageReject()
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway);
		using var client = new TcpClient();
		client.Connect(IPAddress.Loopback, host.Port);
		host.WaitUntilConnected();
		client.ReceiveTimeout = 10000;
		var request = new byte[256];
		var length = new HistoricalPriceDataRequest { RequestID = 71, Symbol = "UNKNOWN", Exchange = "TESTCC" }.ToVls(request, Encoding.UTF8);
		client.GetStream().Write(request.AsSpan(0, length));
		var response = ReadFrame(client);
		Assert.That(DtcWire.TypeOf(response), Is.EqualTo(802));
		DtcVlsDecoder.Decode(response, out Dtc.Protocol.Vls.s_HistoricalPriceDataReject reject);
		var text = DtcVlsDecoder.GetString(response, reject.RejectText, Encoding.UTF8);
		Assert.That(text, Does.Contain("UNKNOWN"));
		Assert.That(text.Any(char.IsControl), Is.False);
	}

	[Test]
	public async Task UnicodeNamesAndQueriesRoundTripThroughTheRealClient()
	{
		using var gateway = new ScriptedTInvestGateway();
		var instrument = ScriptedTInvestGateway.Invent();
		gateway.Instruments.Add(instrument with { Short = instrument.Short with { Name = "Придуманный инструмент" } });
		using var host = new TInvestTestHost(gateway);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		var rows = await client.SearchSymbolsAsync(new SymbolSearchRequest { SearchText = "Придуманный", Exchange = "TESTCC" });
		Assert.That(gateway.LastQuery, Is.EqualTo("Придуманный"));
		Assert.That(rows.Single().Description, Is.EqualTo("Придуманный инструмент (lot 10)"));
	}

	static byte[] ReadFrame(TcpClient client)
	{
		var stream = client.GetStream();
		var header = new byte[4];
		stream.ReadExactly(header);
		var frame = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header)];
		header.CopyTo(frame, 0);
		stream.ReadExactly(frame.AsSpan(4));
		return frame;
	}
}

sealed class TInvestTestHost : IDisposable
{
	readonly StoresProvider _provider;
	readonly ConnectedServer _server;

	public TInvestTestHost(ITInvestGateway gateway, TimeProvider? clock = null, TInvestOptions? options = null)
	{
		_provider = new StoresProvider(null, new TInvestStore(gateway, options ?? new TInvestOptions(), clock), NullLoggerFactory.Instance);
		_server = new ConnectedServer(_provider, clock);
		if (!_server.Start()) throw new InvalidOperationException("Loopback test server could not start.");
	}

	public int Port => ((IPEndPoint)_server.Endpoint).Port;

	public async Task ConnectAsync(DtcClient client)
	{
		await client.ConnectAsync();
		WaitUntilConnected();
	}

	public void WaitUntilConnected() => Assert.That(_server.Connected.Wait(TimeSpan.FromSeconds(10)), Is.True, "The test server did not finish accepting the connection.");

	public DtcClient CreateClient(int heartbeatInterval = 10) => new(new DtcClientSettings
	{
		Host = "127.0.0.1",
		Port = Port,
		// The published provider-v1 profile deliberately refuses broker symbols; MP-1 is later.
		Profile = DtcServerProfile.Generic,
		Encoding = EncodingEnum.BINARY_WITH_VARIABLE_LENGTH_STRINGS,
		SynchronizeTrading = false,
		Reconnect = false,
		HeartbeatIntervalInSeconds = heartbeatInterval,
	});

	public void Dispose()
	{
		_server.Stop();
		_server.Dispose();
		_provider.Dispose();
	}

	sealed class ConnectedServer(StoresProvider provider, TimeProvider? clock) : Server(IPAddress.Loopback, 0, false, provider, provider, provider, NullLoggerFactory.Instance, clock)
	{
		public ManualResetEventSlim Connected { get; } = new();

		protected override void OnConnected(NetCoreServer.TcpSession session)
		{
			base.OnConnected(session);
			Connected.Set();
		}
	}
}
