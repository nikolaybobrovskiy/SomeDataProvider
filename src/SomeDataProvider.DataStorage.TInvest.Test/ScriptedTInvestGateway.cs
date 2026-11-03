namespace SomeDataProvider.DataStorage.TInvest.Test;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SomeDataProvider.DataStorage.TInvest;

public sealed class ScriptedTInvestGateway : ITInvestGateway
{
	public List<(string Ticker, string ClassCode)> Lookups { get; } = new();
	public List<TInvestInstrument> Instruments { get; } = new();
	public int Searches { get; private set; }
	public TInvestRateLimiter Limiter { get; set; } = new();
	public List<TInvestCandleRequest> CandleRequests { get; } = new();
	public Func<TInvestCandleRequest, CancellationToken, Task<IReadOnlyList<TInvestCandle>>>? Candles { get; set; }
	public string? LastQuery { get; private set; }
	public Func<string, string, CancellationToken, Task<TInvestInstrument?>>? Resolve { get; set; }
	public TInvestAccess Access { get; set; } = new(true, new[] { true });
	public Func<CancellationToken, Task<ITInvestMarketDataStream>>? OpenStream { get; set; }
	public Func<string, CancellationToken, Task<TInvestMarketSnapshot>>? Snapshot { get; set; }
	public Func<string, int, CancellationToken, Task<TInvestOrderBook>>? OrderBook { get; set; }
	public List<(string Uid, int Depth)> OrderBookRequests { get; } = new();
	public List<ScriptedMarketDataStream> Streams { get; } = new();
	public Dictionary<(string InstrumentId, DateOnly Day), byte[]> ArchiveFiles { get; } = new();
	public Func<Task>? BeforeArchive { get; set; }
	public List<(string InstrumentId, DateOnly Day)> ArchiveRequests { get; } = new();
	public Func<DateTimeOffset, DateTimeOffset, IReadOnlyList<TInvestTrade>>? LastTrades { get; set; }
	public List<(DateTimeOffset From, DateTimeOffset To)> LastTradeRequests { get; } = new();
	public bool Disposed { get; private set; }
	public Queue<Exception> AccessErrors { get; } = new();

	public static TInvestInstrument Invent(string ticker = "SYNTH", string classCode = "TESTCC", TInvestInstrumentKind kind = TInvestInstrumentKind.Share) => new(new(ticker, classCode, "invented-uid-" + ticker, "Invented instrument", kind, 10, true), 0.01m, "zzz", 0.25m, 1000m, 5m);
	public Task<TInvestAccess> CheckAccessAsync(DateTimeOffset deadline, CancellationToken cancellationToken) =>
		AccessErrors.TryDequeue(out var error) ? Task.FromException<TInvestAccess>(error) : Task.FromResult(Access);
	public Task<TInvestInstrument?> GetInstrumentAsync(string ticker, string classCode, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		Lookups.Add((ticker, classCode));
		return Resolve?.Invoke(ticker, classCode, cancellationToken) ?? Task.FromResult(Instruments.FirstOrDefault(i => i.Short.Ticker == ticker && i.Short.ClassCode == classCode));
	}

	public Task<IReadOnlyList<TInvestInstrumentShort>> FindInstrumentsAsync(string query, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		Searches++;
		LastQuery = query;
		return Task.FromResult<IReadOnlyList<TInvestInstrumentShort>>(Instruments.Select(i => i.Short).ToArray());
	}

	public Task<ITInvestMarketDataStream> OpenMarketDataStreamAsync(CancellationToken cancellationToken)
	{
		if (OpenStream != null)
		{
			return OpenStream(cancellationToken);
		}

		var stream = new ScriptedMarketDataStream();
		lock (Streams)
			Streams.Add(stream);
		return Task.FromResult<ITInvestMarketDataStream>(stream);
	}

	public Task<TInvestMarketSnapshot> GetMarketSnapshotAsync(string uid, bool includeDealer, DateTimeOffset deadline, CancellationToken cancellationToken) => Snapshot?.Invoke(uid, cancellationToken) ?? Task.FromResult(new TInvestMarketSnapshot());
	public Task<TInvestOrderBook> GetOrderBookAsync(string uid, int depth, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		OrderBookRequests.Add((uid, depth));
		return OrderBook?.Invoke(uid, depth, cancellationToken) ?? Task.FromResult(new TInvestOrderBook(uid, default, depth, Array.Empty<TInvestBookLevel>(), Array.Empty<TInvestBookLevel>()));
	}
	public void Dispose()
	{
		Disposed = true;
	}

	public Task<TInvestRateLimiter.Reservation> ReserveCandlesAsync(int calls, DateTimeOffset deadline, CancellationToken cancellationToken) => Limiter.ReserveAsync(TInvestLimitGroup.MarketData, calls, deadline, cancellationToken);
	public Task<TInvestRateLimiter.Reservation> ReserveArchivesAsync(int downloads, DateTimeOffset deadline, CancellationToken cancellationToken) => Limiter.ReserveAsync(TInvestLimitGroup.Archives, downloads, deadline, cancellationToken);

	public async Task<bool> DownloadTradeArchiveAsync(string instrumentId, DateOnly day, string destination, TInvestRateLimiter.Reservation? reservation, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		if (reservation != null) await reservation.AcquireAsync(Limiter, deadline, cancellationToken);
		else await Limiter.AcquireAsync(TInvestLimitGroup.Archives, deadline, cancellationToken);
		lock (ArchiveRequests) ArchiveRequests.Add((instrumentId, day));
		if (BeforeArchive != null) await BeforeArchive();
		if (!ArchiveFiles.TryGetValue((instrumentId, day), out var bytes)) return false;
		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
		await System.IO.File.WriteAllBytesAsync(destination, bytes, cancellationToken);
		return true;
	}

	public async Task<IReadOnlyList<TInvestTrade>> GetLastTradesAsync(string uid, DateTimeOffset from, DateTimeOffset to, TInvestRateLimiter.Reservation reservation, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		await reservation.AcquireAsync(Limiter, deadline, cancellationToken);
		lock (LastTradeRequests) LastTradeRequests.Add((from, to));
		return LastTrades?.Invoke(from, to) ?? Array.Empty<TInvestTrade>();
	}

	public async Task<IReadOnlyList<TInvestCandle>> GetCandlesAsync(TInvestCandleRequest request, TInvestRateLimiter.Reservation reservation, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		await reservation.AcquireAsync(Limiter, deadline, cancellationToken);
		CandleRequests.Add(request);
		return Candles == null ? Array.Empty<TInvestCandle>() : await Candles(request, cancellationToken);
	}
}
