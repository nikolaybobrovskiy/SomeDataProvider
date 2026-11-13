namespace SomeDataProvider.DataStorage.TInvest;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

public sealed partial class TInvestStore
{
	readonly object _catalogLock = new();
	readonly byte[] _catalogCursorKey = RandomNumberGenerator.GetBytes(32);
	Task? _catalogRefresh;
	ITimer? _catalogTimer;
	bool _catalogStale;
	DateTimeOffset _catalogRetryAt;
	TInvestCatalogSnapshot? _catalog;

	public bool SupportsCatalog => _gateway.SupportsCatalog;
	internal Task CatalogRefreshTask
	{
		get
		{
			lock (_catalogLock) return _catalogRefresh ?? Task.CompletedTask;
		}
	}

	public string MakeCatalogCursor(TInvestCatalogSnapshot snapshot, string node, string filter, int size, int offset)
	{
		var data = new byte[84];
		BinaryPrimitives.WriteInt32LittleEndian(data, offset);
		Guid.Parse(snapshot.Version).TryWriteBytes(data.AsSpan(4, 16));
		CatalogBinding(node, filter, size).CopyTo(data, 20);
		HMACSHA256.HashData(_catalogCursorKey, data.AsSpan(0, 52)).CopyTo(data, 52);
		return Convert.ToBase64String(data);
	}

	public int ReadCatalogCursor(TInvestCatalogSnapshot snapshot, string node, string filter, int size, string? cursor)
	{
		if (cursor == null) return 0;
		byte[] data;
		try { data = Convert.FromBase64String(cursor); }
		catch (FormatException) { throw new TInvestCatalogException("InvalidCursor"); }
		if (data.Length != 84 || !CryptographicOperations.FixedTimeEquals(data.AsSpan(52), HMACSHA256.HashData(_catalogCursorKey, data.AsSpan(0, 52))) ||
			!data.AsSpan(20, 32).SequenceEqual(CatalogBinding(node, filter, size))) throw new TInvestCatalogException("InvalidCursor");
		if (new Guid(data.AsSpan(4, 16)).ToString() != snapshot.Version) throw new TInvestCatalogException("SnapshotExpired");
		var offset = BinaryPrimitives.ReadInt32LittleEndian(data);
		if (offset <= 0) throw new TInvestCatalogException("InvalidCursor");
		return offset;
	}

	public async Task<TInvestCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		cancellationToken.ThrowIfCancellationRequested();
		if (!SupportsCatalog) throw new TInvestCatalogException("FeatureUnavailable");
		Task pending;
		lock (_catalogLock)
		{
			StartCatalogRefresh();
			if (_catalog != null && !_catalogStale) return _catalog;
			if (_catalogStale) throw new TInvestCatalogException("CatalogStale");
			pending = _catalogRefresh!;
		}
		// An abandoned request must not cancel another caller's shared materialization.
		await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
		lock (_catalogLock) return !_catalogStale && _catalog != null ? _catalog : throw new TInvestCatalogException("CatalogStale");
	}

	byte[] CatalogBinding(string node, string filter, int size) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Source = "t-invest", _options.Environment, Node = node, Filter = filter, Size = size }));

	void StartCatalogRefresh()
	{
		if (Volatile.Read(ref _disposed) != 0 || _catalogRefresh is { IsCompleted: false } || _clock.GetUtcNow() < _catalogRetryAt) return;
		if (_catalog != null && !_catalogStale && _clock.GetUtcNow() - _catalog.Created < TimeSpan.FromHours(1)) return;
		_catalogRefresh = LoadCatalogAsync();
	}

	async Task LoadCatalogAsync()
	{
		try
		{
			var instruments = new List<TInvestInstrument>();
			var deadline = _clock.GetUtcNow().AddSeconds(60);
			using var budget = NewBudget(deadline, _stop.Token);
			foreach (var kind in new[] { TInvestInstrumentKind.Share, TInvestInstrumentKind.Bond, TInvestInstrumentKind.Etf, TInvestInstrumentKind.Future, TInvestInstrumentKind.Currency })
			{
				var remaining = deadline - _clock.GetUtcNow();
				if (remaining <= TimeSpan.Zero) throw new TimeoutException();
				var list = await _gateway.ListInstrumentsAsync(kind, deadline, budget.Token)
					.WaitAsync(remaining, _clock, budget.Token).ConfigureAwait(false);
				budget.Token.ThrowIfCancellationRequested();
				if (list.Count > 100_000 || instruments.Count + list.Count > 250_000) throw new TInvestCatalogException("CatalogLimit");
				if (list.Any(i => i.Short.Kind != kind)) throw new TInvestCatalogException("InvalidSourceData");
				instruments.AddRange(list);
			}
			var snapshot = new TInvestCatalogSnapshot(instruments, _clock.GetUtcNow());
			lock (_catalogLock)
			{
				_stop.Token.ThrowIfCancellationRequested();
				_catalog = snapshot;
				_catalogStale = false;
				ScheduleCatalogRefresh(TimeSpan.FromHours(1));
			}
		}
		catch (Exception)
		{
			// Retain the last complete snapshot; a failed refresh is never an empty success.
			lock (_catalogLock)
			{
				_catalogStale = true;
				_catalogRetryAt = _clock.GetUtcNow().AddMinutes(1);
				ScheduleCatalogRefresh(TimeSpan.FromMinutes(1));
			}
		}
	}

	void ScheduleCatalogRefresh(TimeSpan delay)
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		_catalogTimer ??= _clock.CreateTimer(_ => { lock (_catalogLock) StartCatalogRefresh(); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		_catalogTimer.Change(delay, Timeout.InfiniteTimeSpan);
	}

	void DisposeCatalog()
	{
		lock (_catalogLock) _catalogTimer?.Dispose();
	}
}
