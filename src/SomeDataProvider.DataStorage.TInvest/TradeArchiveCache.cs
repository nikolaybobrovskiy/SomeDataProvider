namespace SomeDataProvider.DataStorage.TInvest;

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SomeDataProvider.DataStorage.Definitions;

/// <summary>
/// Downloaded archives as they came from the host, under {folder}/{uid}/{date}.archive. A past day's archive does not
/// change, so entries never expire; the cap evicts the least recently used files. Use time is the file's write time,
/// refreshed on every hit with the store's clock.
/// </summary>
public sealed class TradeArchiveCache
{
	const string Extension = ".archive";

	readonly string _folder;
	readonly long _maxBytes;
	readonly TimeProvider _clock;
	readonly object _lock = new();
	readonly Dictionary<string, int> _pins = new(StringComparer.OrdinalIgnoreCase);
	readonly ConditionalWeakTable<ProbeScope, ProbePartition> _probePartitions = new();

	public TradeArchiveCache(string folder, long maxBytes, TimeProvider clock)
	{
		_folder = folder;
		_maxBytes = maxBytes;
		_clock = clock;
	}

	public string Folder => _folder;

	public string PathFor(string uid, DateOnly day)
	{
		var folder = _folder;
		if (ProbeContext.IsProbe)
		{
			var scope = ProbeContext.Current ?? throw new ProbeRejectedException();
			scope.Check();
			folder = _probePartitions.GetValue(scope, owner => new ProbePartition(Path.Combine(_folder, ".probes", Guid.NewGuid().ToString("N")), owner, _lock)).Folder;
		}
		return Path.Combine(folder, Segment(uid), day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + Extension);
	}

	/// <summary>Returns the cached file and marks it as used.</summary>
	public bool TryGet(string uid, DateOnly day, out string path)
	{
		path = PathFor(uid, day);
		lock (_lock)
		{
			try
			{
				if (!File.Exists(path)) return false;
				if (new FileInfo(path).Length > _maxBytes)
				{
					// Larger than the whole cap (the cap was lowered since, or a delete failed earlier): never served.
					try { File.Delete(path); }
					catch (IOException) { }
					catch (UnauthorizedAccessException) { }
					return false;
				}

				File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime);
				return true;
			}
			catch (IOException)
			{
				return false;
			}
		}
	}

	/// <summary>
	/// Protects a cached file from eviction until the returned lease is disposed, so the request that selected it can
	/// still read it. Returns null when the file is gone.
	/// </summary>
	public IDisposable? TryPin(string path)
	{
		path = Path.GetFullPath(path);
		lock (_lock)
		{
			if (!File.Exists(path)) return null;
			_pins[path] = _pins.GetValueOrDefault(path) + 1;
		}

		return new Lease(this, path);
	}

	public void Delete(string path)
	{
		lock (_lock)
		{
			try { File.Delete(path); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}

	/// <summary>Removes the least recently used files until the folder is within the cap. The just-stored file and the files that requests have pinned stay.</summary>
	public void Evict(string keep)
	{
		lock (_lock) EvictLocked(Path.GetFullPath(keep));
	}

	/// <summary>The newest day that has a cached archive for any instrument, used to prune the recorder's day files.</summary>
	public DateOnly? NewestDay()
	{
		lock (_lock)
		{
			if (!Directory.Exists(_folder)) return null;
			return new DirectoryInfo(_folder).EnumerateFiles("*" + Extension, SearchOption.AllDirectories)
				.Where(f => !Path.GetRelativePath(_folder, f.FullName).StartsWith(".probes" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				.Select(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) ? (DateOnly?)day : null)
				.Where(d => d != null).DefaultIfEmpty(null).Max();
		}
	}

	static string Segment(string uid) => uid.Length is > 0 and <= 64 && uid.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
		? uid : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid))).ToLowerInvariant();

	void EvictLocked(string? keep)
	{
		if (!Directory.Exists(_folder)) return;
		var files = new DirectoryInfo(_folder).EnumerateFiles("*" + Extension, SearchOption.AllDirectories).ToList();
		var total = files.Sum(f => f.Length);
		foreach (var file in files.Where(f => !string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase) && !_pins.ContainsKey(f.FullName)).OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName, StringComparer.Ordinal))
		{
			if (total <= _maxBytes) break;
			try
			{
				var length = file.Length;
				file.Delete();
				total -= length;
			}
			catch (IOException)
			{
				// In use elsewhere: it stays, and the next eviction tries again.
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	void Unpin(string path)
	{
		lock (_lock)
		{
			var left = _pins.GetValueOrDefault(path) - 1;
			if (left > 0)
			{
				_pins[path] = left;
				return;
			}

			_pins.Remove(path);
			// Eviction that had to skip this file can proceed now.
			EvictLocked(null);
		}
	}

	sealed class ProbePartition
	{
		public ProbePartition(string folder, ProbeScope owner, object gate)
		{
			Folder = folder;
			// The scope owns this registration through its terminal cancellation. No user file is in this directory.
			_ = owner.Cancellation.UnsafeRegister(_ =>
			{
				lock (gate)
				{
					try { Directory.Delete(folder, true); }
					catch (IOException) { }
					catch (UnauthorizedAccessException) { }
				}
			}, null);
		}

		public string Folder { get; }
	}

	sealed class Lease : IDisposable
	{
		readonly TradeArchiveCache _cache;
		readonly string _path;
		int _disposed;

		public Lease(TradeArchiveCache cache, string path)
		{
			_cache = cache;
			_path = path;
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0) _cache.Unpin(_path);
		}
	}
}
