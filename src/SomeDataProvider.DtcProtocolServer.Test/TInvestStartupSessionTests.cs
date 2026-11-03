namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Dtc.Protocol;
using Dtc.Protocol.Messages;
using Dtc.Protocol.Vls;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;

/// <summary>What DTC clients see while the T-Invest startup check is still being retried.</summary>
[TestFixture]
[NonParallelizable]
public class TInvestStartupSessionTests
{
	[Test]
	public async Task LogonDuringStartupStillAdvertisesDepthAndResubscribe()
	{
		var clock = new ManualTimeProvider();
		using var logs = new RecordingLoggerFactory();
		var gateways = new List<ScriptedTInvestGateway>();
		var provider = TInvestStartupRetryTests.Create(clock, logs, gateways, new TInvestRequestException("T-Invest source request failed."));
		provider.Initialize();
		await TInvestStartupRetryTests.WaitForTimer(clock);
		using var host = new TInvestTestHost(provider, clock);
		await using var client = host.CreateClient();
		await host.ConnectAsync(client);
		Assert.That(provider.TInvestReady, Is.False);
		Assert.That(client.Capabilities!.MarketDepthIsSupported, Is.True, "The source may come up after the logon.");
		Assert.That(client.Capabilities.ResubscribeWhenMarketDataFeedAvailable, Is.True);
	}

	[Test]
	public async Task AStartupThatFinishesBetweenTheStartingAnswerAndItsRegistrationStillTellsTheClient()
	{
		var clock = new ManualTimeProvider();
		using var logs = new RecordingLoggerFactory();
		var gateways = new List<ScriptedTInvestGateway>();
		var provider = TInvestStartupRetryTests.Create(clock, logs, gateways, new TInvestRequestException("T-Invest source request failed."));
		provider.Initialize();
		await TInvestStartupRetryTests.WaitForTimer(clock);
		provider.BeforeStartingReject = () =>
		{
			provider.BeforeStartingReject = null;
			// The retry passes right after the request saw the source as starting, before the session recorded the reject.
			clock.Advance(TimeSpan.FromSeconds(30));
			Assert.That(provider.StartupTask.Wait(TimeSpan.FromSeconds(10)), Is.True);
		};
		using var host = new TInvestTestHost(provider, clock);
		using var client = new TcpClient();
		client.Connect(IPAddress.Loopback, host.Port);
		host.WaitUntilConnected();
		client.ReceiveTimeout = 10_000;
		var bytes = new byte[256];
		var length = new MarketDepthRequest { SymbolID = 11, Symbol = "SYNTH", Exchange = "TESTCC", RequestAction = RequestActionEnum.SUBSCRIBE, NumLevels = 1 }.ToVls(bytes, Encoding.UTF8);
		client.GetStream().Write(bytes.AsSpan(0, length));
		var types = new List<int>();
		while (true)
		{
			var frame = Frame(client);
			var type = DtcWire.TypeOf(frame);
			types.Add(type);
			if (type != DtcConstants.MARKET_DATA_FEED_SYMBOL_STATUS) continue;
			DtcBinaryDecoder.Decode(frame, out s_MarketDataFeedSymbolStatus status);
			Assert.That(status.SymbolID, Is.EqualTo(11));
			Assert.That(status.Status, Is.EqualTo(MarketDataFeedStatusEnum.MARKET_DATA_FEED_AVAILABLE));
			break;
		}

		Assert.That(types, Has.One.EqualTo(DtcConstants.MARKET_DEPTH_REJECT), "The reject comes first, then the feed is reported.");
		await Task.CompletedTask;
	}

	[Test]
	public async Task ASubscriberThatThrowsDoesNotUndoAPublishedStartup()
	{
		var clock = new ManualTimeProvider();
		using var logs = new RecordingLoggerFactory();
		var gateways = new List<ScriptedTInvestGateway>();
		using var provider = TInvestStartupRetryTests.Create(clock, logs, gateways, new TInvestRequestException("T-Invest source request failed."));
		var calls = 0;
		provider.TInvestStarted += () =>
		{
			calls++;
			throw new InvalidOperationException("invented subscriber failure");
		};
		var second = 0;
		provider.TInvestStarted += () => second++;
		provider.Initialize();
		await TInvestStartupRetryTests.WaitForTimer(clock);
		clock.Advance(TimeSpan.FromSeconds(30));
		await provider.StartupTask.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.That(provider.TInvestReady, Is.True);
		Assert.That(provider.MarketDataHub, Is.Not.Null);
		Assert.That(gateways, Has.Count.EqualTo(2));
		Assert.That(gateways.Last().Disposed, Is.False, "The published source keeps its gateway.");
		Assert.That((calls, second), Is.EqualTo((1, 1)), "Every subscriber runs, once.");
	}

	static byte[] Frame(TcpClient client)
	{
		var header = new byte[4];
		client.GetStream().ReadExactly(header);
		var frame = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header)];
		header.CopyTo(frame, 0);
		client.GetStream().ReadExactly(frame.AsSpan(4));
		return frame;
	}
}
