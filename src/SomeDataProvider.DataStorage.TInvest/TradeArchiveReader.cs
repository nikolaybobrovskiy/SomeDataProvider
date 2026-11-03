namespace SomeDataProvider.DataStorage.TInvest;

using System.Globalization;
using System.IO.Compression;

/// <summary>Reads one downloaded instrument-day archive: gzip or ZIP (by magic bytes) around a seven-column CSV.</summary>
public static class TradeArchiveReader
{
	/// <summary>The most bytes one archive may expand to. A real day is far below it; the limit stops a decompression bomb.</summary>
	public const long MaximumInflatedBytes = 4L * 1024 * 1024 * 1024;

	public const string Header = "TRADE_TS,TICKER_CC,DIRECTION,PRICE,QUANTITY,TRADE_SOURCE,INSTRUMENT_UID";

	/// <summary>The only formats accepted, from the first bytes of a file.</summary>
	public static bool IsKnownFormat(ReadOnlySpan<byte> start) =>
		(start.Length >= 2 && start[0] == 0x1F && start[1] == 0x8B) || (start.Length >= 4 && start[0] == 0x50 && start[1] == 0x4B && start[2] == 0x03 && start[3] == 0x04);

	/// <summary>Counts rows kept by the dealer rule inside [start, end). A null bound is open. Fails on any malformed row.</summary>
	public static int Count(string path, DateOnly day, bool includeDealer, DateTimeOffset? start, DateTimeOffset? end, int stopAfter = int.MaxValue, CancellationToken cancellationToken = default)
	{
		var count = 0;
		foreach (var row in Rows(path, day, cancellationToken))
		{
			if (!includeDealer && !row.IsExchange) continue;
			if ((start != null && row.Time.Utc < start) || (end != null && row.Time.Utc >= end)) continue;
			if (++count > stopAfter) break;
		}

		return count;
	}

	/// <summary>Loads the day's kept rows inside [start, end), stably sorted by trade time.</summary>
	public static List<TInvestTick> Load(string path, DateOnly day, bool includeDealer, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default)
	{
		var result = new List<TInvestTick>();
		var sorted = true;
		foreach (var row in Rows(path, day, cancellationToken))
		{
			if (!includeDealer && !row.IsExchange) continue;
			if (row.Time.Utc < start || row.Time.Utc >= end) continue;
			if (result.Count > 0 && Compare(result[^1].Time, row.Time) > 0) sorted = false;
			result.Add(new(row.Time, row.Price, row.Lots, row.Direction));
		}

		return sorted ? result : result.Select((tick, index) => (tick, index)).OrderBy(p => p.tick.Time.Seconds).ThenBy(p => p.tick.Time.Nanos).ThenBy(p => p.index).Select(p => p.tick).ToList();
	}

	internal static IEnumerable<Row> Rows(string path, DateOnly day, CancellationToken cancellationToken = default)
	{
		using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536);
		var magic = new byte[4];
		var read = file.Read(magic, 0, 4);
		file.Position = 0;
		if (!IsKnownFormat(magic.AsSpan(0, read))) throw Malformed();
		ZipArchive? zip = null;
		Stream csv;
		try
		{
			if (magic[0] == 0x1F)
			{
				csv = new GZipStream(file, CompressionMode.Decompress, true);
			}
			else
			{
				zip = new ZipArchive(file, ZipArchiveMode.Read, true);
				if (zip.Entries.Count != 1) throw Malformed();
				csv = zip.Entries[0].Open();
			}
		}
		catch (InvalidDataException)
		{
			zip?.Dispose();
			throw Malformed();
		}

		using (zip)
		using (csv)
		using (var reader = new StreamReader(new LimitedStream(csv, MaximumInflatedBytes)))
		{
			var dayStart = (day.DayNumber - 719162) * 86400L;
			var first = Next(reader);
			if (first == null || first.TrimStart('\uFEFF') != Header) throw Malformed();
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var line = Next(reader);
				if (line == null) yield break;
				if (line.Length == 0) continue;
				if (!TryParse(line, out var row) || row.Time.Seconds < dayStart || row.Time.Seconds >= dayStart + 86400) throw Malformed();
				yield return row;
			}
		}
	}

	static int Compare(TInvestTimestamp a, TInvestTimestamp b) => a.Seconds != b.Seconds ? a.Seconds.CompareTo(b.Seconds) : a.Nanos.CompareTo(b.Nanos);

	static string? Next(StreamReader reader)
	{
		try
		{
			return reader.ReadLine();
		}
		catch (InvalidDataException)
		{
			throw Malformed();
		}
	}

	static TInvestRequestException Malformed() => new("T-Invest trade archive has an unexpected format.");

	static bool TryParse(string line, out Row row)
	{
		row = default;
		var text = line.AsSpan();
		Span<int> commas = stackalloc int[7];
		var found = 0;
		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] != ',') continue;
			if (found == 6) return false;
			commas[found++] = i;
		}

		if (found != 6 || !TryTime(text[..commas[0]], out var time)) return false;
		var direction = text[(commas[1] + 1)..commas[2]];
		if (!double.TryParse(text[(commas[2] + 1)..commas[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out var price) ||
			!long.TryParse(text[(commas[3] + 1)..commas[4]], NumberStyles.None, CultureInfo.InvariantCulture, out var lots) ||
			!double.IsFinite(price) || price < 0 || lots <= 0)
			return false;
		var side = direction.EndsWith("BUY", StringComparison.Ordinal) || direction is "1" ? TInvestDirection.Buy
			: direction.EndsWith("SELL", StringComparison.Ordinal) || direction is "2" ? TInvestDirection.Sell : TInvestDirection.Unknown;
		row = new(time, price, lots, side, text[(commas[4] + 1)..commas[5]].SequenceEqual("EXCHANGE"));
		return true;
	}

	/// <summary>Parses yyyy-MM-ddTHH:mm:ss[.fraction]Z by position, falling back to the general UTC parser.</summary>
	static bool TryTime(ReadOnlySpan<char> text, out TInvestTimestamp time)
	{
		time = default;
		if (text.Length >= 20 && text[4] == '-' && text[7] == '-' && text[10] is 'T' or ' ' && text[13] == ':' && text[16] == ':' && text[^1] == 'Z' &&
			int.TryParse(text[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) && int.TryParse(text[5..7], NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
			int.TryParse(text[8..10], NumberStyles.None, CultureInfo.InvariantCulture, out var dayOfMonth) && int.TryParse(text[11..13], NumberStyles.None, CultureInfo.InvariantCulture, out var hour) &&
			int.TryParse(text[14..16], NumberStyles.None, CultureInfo.InvariantCulture, out var minute) && int.TryParse(text[17..19], NumberStyles.None, CultureInfo.InvariantCulture, out var second) &&
			year >= 1970 && month is >= 1 and <= 12 && dayOfMonth >= 1 && dayOfMonth <= DateTime.DaysInMonth(year, month) && hour < 24 && minute < 60 && second < 60)
		{
			var nanos = 0;
			if (text.Length > 20)
			{
				var fraction = text[19..^1];
				if (fraction.Length < 2 || fraction.Length > 10 || fraction[0] != '.' || !int.TryParse(fraction[1..], NumberStyles.None, CultureInfo.InvariantCulture, out nanos)) return false;
				for (var i = fraction.Length - 1; i < 9; i++) nanos *= 10;
			}
			else if (text.Length != 20)
			{
				return false;
			}

			var seconds = (new DateOnly(year, month, dayOfMonth).DayNumber - 719162) * 86400L + hour * 3600 + minute * 60 + second;
			time = new(seconds, nanos);
			return true;
		}

		if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) return false;
		time = new(parsed.ToUnixTimeSeconds(), (int)(parsed.Ticks % TimeSpan.TicksPerSecond) * 100);
		return true;
	}

	internal readonly record struct Row(TInvestTimestamp Time, double Price, long Lots, TInvestDirection Direction, bool IsExchange);

	sealed class LimitedStream(Stream inner, long limit) : Stream
	{
		long _read;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

		public override int Read(Span<byte> buffer)
		{
			var read = inner.Read(buffer);
			if ((_read += read) > limit) throw new TInvestRequestException("T-Invest trade archive expands beyond the supported maximum.");
			return read;
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
