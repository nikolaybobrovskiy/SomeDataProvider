namespace SomeDataProvider.DataStorage.TInvest.Test;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;

/// <summary>Synthetic archives and a fake clock. Invented ticker and class code; no recorded data.</summary>
sealed class TickFixture : IDisposable
{
	public static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero); // A Friday.
	public static readonly string InstrumentId = "SYNTH_TESTCC";

	public TickFixture(int maxDays = 31, int maxTicks = 5_000_000, bool dealer = false, DateTimeOffset? firstCandle = null, long cacheBytes = 20L * 1024 * 1024 * 1024)
	{
		Folder = Path.Combine(Path.GetTempPath(), "sdp-ticks-" + Guid.NewGuid().ToString("N"));
		Clock.Advance(Now - DateTimeOffset.UnixEpoch);
		Gateway = new ScriptedTInvestGateway { Limiter = new(Clock) };
		Gateway.Instruments.Add(ScriptedTInvestGateway.Invent() with { FirstMinuteCandle = firstCandle ?? new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) });
		Store = new TInvestStore(Gateway, new TInvestOptions { ArchiveCacheFolder = Folder, MaxTickDaysPerRequest = maxDays, MaxTicksPerRequest = maxTicks, IncludeDealerTrades = dealer, ArchiveCacheMaxBytes = cacheBytes }, Clock);
		Store.UseTickRecording(Recording);
		Symbol = Store.ResolveAsync("SYNTH", "TESTCC", Clock.GetUtcNow().AddSeconds(8)).GetAwaiter().GetResult()!;
	}

	public ManualTimeProvider Clock { get; } = new();
	public ScriptedTInvestGateway Gateway { get; }
	public TInvestStore Store { get; }
	public TInvestSymbol Symbol { get; }
	public FakeRecording Recording { get; } = new();
	public string Folder { get; }

	public static DateTime Utc(int month, int day, int hour = 0, int minute = 0, int second = 0) => new(2026, month, day, hour, minute, second, DateTimeKind.Utc);

	public static string Csv(params string[] rows) => TradeArchiveReader.Header + "\n" + string.Join("\n", rows) + "\n";

	public static string Row(string time, string direction, string price, long quantity, string source = "EXCHANGE") => $"{time},SYNTH_TESTCC,{direction},{price},{quantity},{source},invented-uid-SYNTH";

	public static byte[] Gzip(string csv)
	{
		using var output = new MemoryStream();
		using (var zip = new GZipStream(output, CompressionLevel.Fastest, true)) zip.Write(Encoding.UTF8.GetBytes(csv));
		return output.ToArray();
	}

	public static byte[] Zip(string csv, int entries = 1)
	{
		using var output = new MemoryStream();
		using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
		{
			for (var i = 0; i < entries; i++)
			{
				using var entry = new StreamWriter(zip.CreateEntry($"trades{i}.csv").Open(), new UTF8Encoding(false));
				entry.Write(csv);
			}
		}

		return output.ToArray();
	}

	public static async Task<List<TInvestTickRecord>> ReadAll(TInvestTickReader reader)
	{
		var result = new List<TInvestTickRecord>();
		while (true)
		{
			var batch = await reader.ReadSymbolHistoryAsync();
			result.AddRange(batch.Records.Cast<TInvestTickRecord>());
			if (!batch.HasMore) return result;
		}
	}

	public static long Micros(int month, int day, int hour, int minute, int second, int micro = 0) => new DateTimeOffset(2026, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds() * 1_000_000 + micro;

	public static TInvestTrade Trade(DateTime time, int nanos, double price, long lots, bool exchange = true, TInvestDirection direction = TInvestDirection.Buy) =>
		new("invented-uid-SYNTH", new(new DateTimeOffset(time).ToUnixTimeSeconds(), nanos), price, lots, direction, exchange);

	public void Archive(int month, int day, byte[] bytes) => Gateway.ArchiveFiles[(InstrumentId, new DateOnly(2026, month, day))] = bytes;

	public DateTimeOffset Deadline() => Clock.GetUtcNow().AddSeconds(20);

	public Task<TInvestTickReader> Ticks(DateTime start, DateTime end = default) => Store.CreateTickReaderAsync(Symbol, start, end, Deadline());

	public void Dispose()
	{
		Store.Dispose();
		try { Directory.Delete(Folder, true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	/// <summary>A recorder stand-in with scripted coverage and trades.</summary>
	public sealed class FakeRecording : ITickRecording
	{
		public List<(DateTimeOffset Start, DateTimeOffset End)> Intervals { get; } = new();
		public List<TInvestTrade> Trades { get; } = new();
		public DateOnly? DeletedBefore { get; private set; }
		public Action? OnRead { get; set; }

		public IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Coverage(string uid, DateOnly day) => Intervals.ToArray();

		public IReadOnlyList<TInvestTrade> Read(string uid, DateOnly day, DateTimeOffset start, DateTimeOffset end)
		{
			OnRead?.Invoke();
			return Trades.Where(t => t.Time.Utc >= start && t.Time.Utc < end).ToArray();
		}

		public void DeleteBefore(DateOnly newestArchivedDay) => DeletedBefore = newestArchivedDay;
	}
}
