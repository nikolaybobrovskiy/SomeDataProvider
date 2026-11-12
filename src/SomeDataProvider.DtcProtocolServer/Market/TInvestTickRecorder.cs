namespace SomeDataProvider.DtcProtocolServer.Market;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using SomeDataProvider.DataStorage.TInvest;

/// <summary>Exchange ticks and progress-bounded UTC coverage. Disk work never holds the hub-facing lock.</summary>
sealed class TInvestTickRecorder : IDisposable, ITickRecording
{
	const int CoverageProofVersion = 1;
	const string CoverageSource = "t-invest:exchange";

	readonly object _gate = new();
	readonly object _ioGate = new();
	readonly string? _root;
	readonly TimeProvider _clock;
	readonly Action? _beforeWrite;
	readonly Dictionary<string, (DateTimeOffset Start, DateTimeOffset Watermark)> _live = new(StringComparer.Ordinal);
	readonly Dictionary<(string Uid, DateOnly Day), List<CoverageInterval>> _coverage = new();
	readonly HashSet<(string Uid, DateOnly Day)> _dirty = new();
	readonly Queue<TInvestTrade> _pending = new();
	readonly Dictionary<(string Uid, DateOnly Day), BinaryWriter> _writers = new();
	readonly Dictionary<(string Uid, DateOnly Day), List<CoverageInterval>> _persisted = new();
	readonly ITimer _flush;
	volatile bool _failed;
	bool _disposed;
	bool _closing;
	// A null root is in-memory coverage only, for scripted tests and structure-only live probes.
	public TInvestTickRecorder(string? root, TimeProvider clock, Action? beforeWrite = null)
	{
		_root = root;
		_clock = clock;
		_beforeWrite = beforeWrite;
		_flush = clock.CreateTimer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}

	public event Action? RecordingFailed;
	public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(SomeDataProvider), "tinvest-today");
	public bool Failed => _failed;
	public long RecordedTrades { get; private set; }

	public void SetLive(string uid, bool live)
	{
		lock (_gate)
		{
			if (_closing || Failed)
			{
				return;
			}

			if (live)
			{
				var now = _clock.GetUtcNow();
				_live.TryAdd(uid, (now, now));
			}
			else if (_live.Remove(uid, out var interval))
			{
				AddCoverage(uid, interval.Start, interval.Watermark);
			}
		}
	}

	// Receipt proves progress only up to a one-second latency margin, never up to watchdog detection time.
	public void Confirm(string uid, DateTimeOffset received)
	{
		lock (_gate)
		{
			if (_live.TryGetValue(uid, out var interval))
			{
				var watermark = received.AddSeconds(-1);
				if (watermark > interval.Watermark)
				{
					_live[uid] = (interval.Start, watermark);
				}
			}
		}
	}

	public void Record(TInvestTrade trade)
	{
		if (!trade.IsExchange)
		{
			return;
		}

		lock (_gate)
		{
			if (_closing || Failed)
			{
				return;
			}

			if (_pending.Count == 100_000)
			{
				Fail();
				return;
			}

			_pending.Enqueue(trade);
			RecordedTrades++;
		}
	}

	public IReadOnlyList<CoverageInterval> Coverage(string uid, DateOnly day)
	{
		List<CoverageInterval> persisted;
		lock (_ioGate)
			persisted = Load(uid, day).ToList();
		lock (_gate)
			return Merge(persisted.Concat(_coverage.GetValueOrDefault((uid, day), new())));
	}

	// Without a folder nothing is durable, so nothing is proven: history never serves from an in-memory recorder.
	IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ITickRecording.Coverage(string uid, DateOnly day) =>
		_root == null ? Array.Empty<(DateTimeOffset, DateTimeOffset)>() : Coverage(uid, day).Select(c => (c.Start, c.End)).ToArray();

	/// <summary>Flushes queued trades, then reads the day's file. Only whole 29-byte records count.</summary>
	IReadOnlyList<TInvestTrade> ITickRecording.Read(string uid, DateOnly day, DateTimeOffset start, DateTimeOffset end)
	{
		if (_root == null)
		{
			return Array.Empty<TInvestTrade>();
		}

		lock (_ioGate)
		{
			FlushCore();
			var path = DayPath(uid, day) + ".bin";
			var result = new List<TInvestTrade>();
			if (!File.Exists(path))
			{
				return result;
			}

			using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var reader = new BinaryReader(file);
			for (var index = 0L; index < file.Length / 29; index++)
			{
				var time = new TInvestTimestamp(reader.ReadInt64(), reader.ReadInt32());
				var price = reader.ReadDouble();
				var lots = reader.ReadInt64();
				var direction = (TInvestDirection)reader.ReadByte();
				if (time.Utc >= start && time.Utc < end)
				{
					result.Add(new TInvestTrade(uid, time, price, lots, direction, true));
				}
			}

			return result;
		}
	}

	public void Flush()
	{
		// Slow I/O cannot pile up timer callbacks or block Record/SetLive/Confirm.
		if (!Monitor.TryEnter(_ioGate))
		{
			return;
		}

		try
		{
			FlushCore();
		}
		finally
		{
			Monitor.Exit(_ioGate);
		}
	}

	// Stage 5b calls this only after a day's archive is durably available.
	public void DeleteBefore(DateOnly newestArchivedDay)
	{
		lock (_ioGate)
		{
			FlushCore();
			foreach (var key in _writers.Keys.Where(k => k.Day < newestArchivedDay).ToArray())
			{
				_writers[key].Dispose();
				_writers.Remove(key);
			}

			foreach (var key in _persisted.Keys.Where(k => k.Day < newestArchivedDay).ToArray())
			{
				_persisted.Remove(key);
			}

			lock (_gate)
			{
				foreach (var key in _coverage.Keys.Where(k => k.Day < newestArchivedDay).ToArray())
				{
					_coverage.Remove(key);
					_dirty.Remove(key);
				}
			}

			if (_root == null || !Directory.Exists(_root))
			{
				return;
			}

			foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
			{
				if (DateOnly.TryParseExact(Path.GetFileName(file).Split('.')[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) && day < newestArchivedDay)
				{
					File.Delete(file);
				}
			}
		}
	}

	public void Dispose()
	{
		_flush.Dispose();
		lock (_ioGate)
		{
			lock (_gate)
			{
				if (_disposed)
				{
					return;
				}

				_closing = true;
				foreach (var (uid, interval) in _live)
				{
					AddCoverage(uid, interval.Start, interval.Watermark);
				}

				_live.Clear();
			}

			FlushCore();
			lock (_gate)
				_disposed = true;
			foreach (var writer in _writers.Values)
			{
				writer.Dispose();
			}

			_writers.Clear();
		}
	}

	static IEnumerable<(DateOnly Day, CoverageInterval Interval)> Split(DateTimeOffset start, DateTimeOffset end)
	{
		while (start < end)
		{
			var next = new DateTimeOffset(start.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
			var stop = end < next ? end : next;
			yield return (DateOnly.FromDateTime(start.UtcDateTime), new(start, stop));
			start = stop;
		}
	}

	static List<CoverageInterval> Merge(IEnumerable<CoverageInterval> intervals)
	{
		var result = new List<CoverageInterval>();
		foreach (var item in intervals.Where(i => i.Start < i.End).OrderBy(i => i.Start))
		{
			if (result.Count != 0 && result[^1].End >= item.Start)
			{
				result[^1] = new(result[^1].Start, result[^1].End > item.End ? result[^1].End : item.End);
			}
			else
			{
				result.Add(item);
			}
		}

		return result;
	}

	void FlushCore()
	{
		TInvestTrade[] trades;
		Dictionary<(string Uid, DateOnly Day), List<CoverageInterval>> changed;
		lock (_gate)
		{
			if (_disposed || Failed)
			{
				return;
			}

			foreach (var (uid, interval) in _live.ToArray())
			{
				AddCoverage(uid, interval.Start, interval.Watermark);
				_live[uid] = (interval.Watermark, interval.Watermark);
			}

			trades = _pending.ToArray();
			_pending.Clear();
			changed = _dirty.ToDictionary(key => key, key => _coverage[key].ToList());
			_dirty.Clear();
		}

		if (_root == null)
		{
			return;
		}

		try
		{
			_beforeWrite?.Invoke();
			foreach (var trade in trades)
			{
				Write(trade);
			}

			foreach (var writer in _writers.Values)
			{
				writer.Flush();
				((FileStream)writer.BaseStream).Flush(true);
			}

			foreach (var (key, intervals) in changed)
			{
				var merged = Merge(Load(key.Uid, key.Day).Concat(intervals));
				var path = DayPath(key.Uid, key.Day) + ".coverage.json";
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(path + ".new", JsonSerializer.Serialize(new CoverageManifest(CoverageProofVersion, key.Uid, key.Day, CoverageSource, merged)));
				File.Move(path + ".new", path, true);
				_persisted[key] = merged;
			}

			var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
			foreach (var key in _writers.Keys.Where(k => k.Day < today).ToArray())
			{
				_writers[key].Dispose();
				_writers.Remove(key);
			}
		}
		catch (IOException)
		{
			Fail();
		}
		catch (UnauthorizedAccessException)
		{
			Fail();
		}
	}

	void Write(TInvestTrade trade)
	{
		var key = (trade.Uid, Day: DateOnly.FromDateTime(trade.Time.Utc.UtcDateTime));
		if (!_writers.TryGetValue(key, out var writer))
		{
			var path = DayPath(key.Uid, key.Day);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			// Fixed 29-byte records. Remove a torn tail before appending after a crash.
			var file = new FileStream(path + ".bin", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
			file.SetLength(file.Length / 29 * 29);
			file.Position = file.Length;
			_writers[key] = writer = new BinaryWriter(file);
		}

		writer.Write(trade.Time.Seconds);
		writer.Write(trade.Time.Nanos);
		writer.Write(trade.Price);
		writer.Write(trade.Lots);
		writer.Write((byte)trade.Direction);
	}

	List<CoverageInterval> Load(string uid, DateOnly day)
	{
		var key = (uid, day);
		if (_persisted.TryGetValue(key, out var existing))
		{
			return existing;
		}

		var result = new List<CoverageInterval>();
		if (_root != null)
		{
			var path = DayPath(uid, day) + ".coverage.json";
			if (File.Exists(path))
			{
				try
				{
					// The unversioned contract predates EmptyDayProofV1. Never import its claims, even when
					// its trades are retained; only fresh source-matched coverage can replace them.
					var manifest = JsonSerializer.Deserialize<CoverageManifest>(File.ReadAllText(path));
					var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
					if (manifest is { ProofVersion: CoverageProofVersion, Source: CoverageSource, Intervals: not null } &&
						manifest.InstrumentUid == uid && manifest.Day == day &&
						manifest.Intervals.All(i => i.Start >= start && i.Start < i.End && i.End <= start.AddDays(1)))
						result = manifest.Intervals;
				}
				catch (JsonException)
				{ /* An unreadable manifest never proves coverage. */
				}
			}
		}

		return _persisted[key] = result;
	}

	string DayPath(string uid, DateOnly day) => Path.Combine(_root!, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid))).ToLowerInvariant(), day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
	void AddCoverage(string uid, DateTimeOffset start, DateTimeOffset end)
	{
		foreach (var part in Split(start, end))
		{
			var key = (uid, part.Day);
			_coverage[key] = Merge(_coverage.GetValueOrDefault(key, new()).Append(part.Interval));
			_dirty.Add(key);
		}
	}

	void Fail()
	{
		lock (_gate)
		{
			_failed = true;
			_live.Clear();
			_pending.Clear();
			// In-memory extensions do not prove durable coverage after a failed flush.
			_coverage.Clear();
			_dirty.Clear();
		}

		RecordingFailed?.Invoke();
	}

	sealed record CoverageManifest(int ProofVersion, string InstrumentUid, DateOnly Day, string Source, List<CoverageInterval> Intervals);
}
