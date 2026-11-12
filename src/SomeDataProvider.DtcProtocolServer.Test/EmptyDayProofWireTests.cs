namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dtc.Protocol.Private;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DataStorage.TInvest.Test;
using SomeDataProvider.DtcProtocolServer.Market;

public partial class ProviderInformationWireTests
{
	[TestCase(28)]
	[TestCase(29)]
	public void EmptyDayCapabilityAndAllAgeUnavailableRuleShipTogether(int day)
	{
		var clock = new ManualTimeProvider();
		clock.Advance(TickFixture.Now - DateTimeOffset.UnixEpoch);
		var root = Path.Combine(Path.GetTempPath(), "sdp-proof-" + Guid.NewGuid().ToString("N"));
		try
		{
			using var gateway = new ScriptedTInvestGateway { Limiter = new(clock) };
			gateway.Instruments.Add(ScriptedTInvestGateway.Invent() with { FirstMinuteCandle = TickFixture.Now.AddYears(-1) });
			using var host = new TInvestTestHost(gateway, clock, new() { ArchiveCacheFolder = root });
			using var client = Connect(host);
			Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-generation" });
			var hello = (ProviderHelloResponse)Read(client);
			Assert.That(hello.Features & ProviderInformationFeatures.EmptyDayProofV1, Is.Not.Zero);
			Assert.That(hello.Features & ProviderInformationFeatures.RecentClosedTicksV1, Is.Zero);
			var start = new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero);
			client.GetStream().Write(DtcWire.EncodeHistoricalPriceDataRequest(91, "SYNTH-TESTCC", start.ToUnixTimeSeconds(), start.AddDays(1).ToUnixTimeSeconds(), 0));
			var frame = ReadStandardFrame(client);
			Assert.That(DtcWire.TypeOf(frame), Is.EqualTo(Dtc.Protocol.DtcConstants.HISTORICAL_PRICE_DATA_REJECT), "No empty header without proof, including on the same negotiated binary.");
			Assert.That(gateway.ArchiveRequests, Has.Count.EqualTo(1));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}

	static byte[] ReadStandardFrame(TcpClient client)
	{
		var header = new byte[4];
		client.GetStream().ReadExactly(header);
		var frame = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header)];
		header.CopyTo(frame, 0);
		client.GetStream().ReadExactly(frame.AsSpan(4));
		return frame;
	}
}

[TestFixture]
public class EmptyDayProofUpgradeTests
{
	[TestCase("legacy")]
	[TestCase("version")]
	[TestCase("uid")]
	[TestCase("day")]
	[TestCase("source")]
	public void UpgradeIgnoresOldCoverageButPreservesRecordedTrades(string mismatch)
	{
		var clock = new ManualTimeProvider();
		clock.Advance(TickFixture.Now - DateTimeOffset.UnixEpoch);
		var root = Path.Combine(Path.GetTempPath(), "sdp-proof-upgrade-" + Guid.NewGuid().ToString("N"));
		var uid = "invented-uid-SYNTH";
		var day = new DateOnly(2026, 10, 28);
		var start = new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
		var folder = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid))).ToLowerInvariant());
		Directory.CreateDirectory(folder);
		var path = Path.Combine(folder, "2026-10-28");
		var intervals = new[] { new CoverageInterval(start, start.AddDays(1)) };
		File.WriteAllText(path + ".coverage.json", mismatch == "legacy" ? JsonSerializer.Serialize(intervals) : JsonSerializer.Serialize(new
		{
			ProofVersion = mismatch == "version" ? 0 : 1, InstrumentUid = mismatch == "uid" ? "invented-other" : uid,
			Day = mismatch == "day" ? day.AddDays(1) : day, Source = mismatch == "source" ? "t-invest:dealer" : "t-invest:exchange", Intervals = intervals,
		}));
		using (var writer = new BinaryWriter(File.Create(path + ".bin")))
		{
			writer.Write(start.ToUnixTimeSeconds());
			writer.Write(0);
			writer.Write(12.5);
			writer.Write(1L);
			writer.Write((byte)TInvestDirection.Buy);
		}
		try
		{
			using var fixture = new TickFixture();
			using var recorder = new TInvestTickRecorder(root, clock);
			ITickRecording recording = recorder;
			Assert.That(recording.Coverage(uid, day), Is.Empty, "Unversioned claims from the old proof contract are not adopted.");
			Assert.That(recording.Read(uid, day, start, start.AddDays(1)).Single().Price, Is.EqualTo(12.5));
			fixture.Store.UseTickRecording(recording);
			Assert.ThrowsAsync<TInvestRequestException>(() => fixture.Ticks(TickFixture.Utc(10, 28), TickFixture.Utc(10, 29)));
			Assert.That(File.Exists(path + ".bin"), Is.True);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Test]
	public void FreshCoverageReplacesOldClaimsWithoutMergingAndSurvivesRestart()
	{
		var root = Path.Combine(Path.GetTempPath(), "sdp-proof-fresh-" + Guid.NewGuid().ToString("N"));
		var clock = new ManualTimeProvider();
		clock.Advance(TickFixture.Now - DateTimeOffset.UnixEpoch);
		var uid = "invented-uid-SYNTH";
		var day = new DateOnly(2026, 10, 30);
		var folder = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid))).ToLowerInvariant());
		Directory.CreateDirectory(folder);
		File.WriteAllText(Path.Combine(folder, "2026-10-30.coverage.json"), JsonSerializer.Serialize(new[] { new CoverageInterval(TickFixture.Now.AddHours(-12), TickFixture.Now) }));
		try
		{
			using (var recorder = new TInvestTickRecorder(root, clock))
			{
				recorder.SetLive(uid, true);
				clock.Advance(TimeSpan.FromSeconds(6));
				recorder.Confirm(uid, clock.GetUtcNow());
				recorder.Flush();
				Assert.That(recorder.Coverage(uid, day).Single(), Is.EqualTo(new CoverageInterval(TickFixture.Now, TickFixture.Now.AddSeconds(5))));
			}
			using var restarted = new TInvestTickRecorder(root, clock);
			Assert.That(((ITickRecording)restarted).Coverage(uid, day).Single(), Is.EqualTo((TickFixture.Now, TickFixture.Now.AddSeconds(5))));
			Assert.That(((ITickRecording)restarted).Coverage("invented-other", day), Is.Empty);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
