namespace SomeDataProvider.DataStorage.TInvest;

using SomeDataProvider.DataStorage.Definitions;

public sealed partial class TInvestStore
{
	public async Task<TInvestHistoryReader> CreateCandleReaderAsync(TInvestSymbol symbol, int intervalSeconds, DateTime start, DateTime end, DateTimeOffset deadline, CancellationToken ct = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		var plan = TInvestCandlePlan.Create(symbol, intervalSeconds, start, end, _clock.GetUtcNow(), _options);
		using var budget = NewBudget(deadline, ct);
		try
		{
			var reservation = await _gateway.ReserveCandlesAsync(plan.Calls.Count, deadline, budget.Token).ConfigureAwait(false);
			return new TInvestHistoryReader(_gateway, plan, symbol.Instrument.Short.Lot, reservation, _clock, _stop.Token);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_stop.IsCancellationRequested)
		{
			throw Limited();
		}
	}
}

/// <summary>Reads bounded time pages. The streamer sets one deadline across empty pages and read-ahead.</summary>
public sealed class TInvestHistoryReader : ISymbolHistoryStoreReader
{
	readonly ITInvestGateway _gateway;
	readonly TInvestCandlePlan _plan;
	readonly int _lot;
	readonly TInvestRateLimiter.Reservation _reservation;
	readonly TimeProvider _clock;
	readonly CancellationTokenSource _stop;
	int _index;
	int _disposed;
	DateTimeOffset? _last;
	TInvestCandle? _incomplete;

	internal TInvestHistoryReader(ITInvestGateway gateway, TInvestCandlePlan plan, int lot, TInvestRateLimiter.Reservation reservation, TimeProvider clock, CancellationToken stop)
	{
		_gateway = gateway;
		_plan = plan;
		_lot = lot;
		_reservation = reservation;
		_clock = clock;
		_stop = CancellationTokenSource.CreateLinkedTokenSource(stop);
		ReadDeadline = _clock.GetUtcNow().AddSeconds(20);
	}

	public DateTimeOffset ReadDeadline { get; set; }
	public TimeProvider Clock => _clock;

	public async Task<SymbolHistoryResponse> ReadSymbolHistoryAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (_index == _plan.Calls.Count) return SymbolHistoryResponse.Empty;
		using var timer = new CancellationTokenSource(Max(ReadDeadline - _clock.GetUtcNow()), _clock);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken, _stop.Token);
		try
		{
			var request = _plan.Calls[_index];
			var candles = await _gateway.GetCandlesAsync(request, _reservation, ReadDeadline, linked.Token)
				.WaitAsync(Max(ReadDeadline - _clock.GetUtcNow()), _clock, linked.Token).ConfigureAwait(false);
			linked.Token.ThrowIfCancellationRequested();
			if (_clock.GetUtcNow() >= ReadDeadline) throw new TimeoutException();
			var inRange = candles.Where(c => c.Time >= request.Start && c.Time < request.End && c.Time >= _plan.Start && c.Time < _plan.End).ToArray();
			if (inRange.Length > request.Limit) throw new TInvestRequestException("T-Invest returned too many candles.");
			var rows = new List<ISymbolHistoryRecord>();
			foreach (var candle in inRange.OrderBy(c => c.Time).DistinctBy(c => c.Time))
			{
				if (!request.IncludeDealer && !candle.Exchange) continue;
				if (_last >= candle.Time) continue;
				if (!candle.Complete)
				{
					if (_incomplete == null || candle.Time > _incomplete.Time) _incomplete = candle;
					continue;
				}
				rows.Add(Bar(candle));
				_last = candle.Time;
			}
			_index++;
			var more = _index < _plan.Calls.Count;
			if (!more && _plan.OpenEnded && _incomplete != null && (_last == null || _incomplete.Time > _last)) rows.Add(Bar(_incomplete));
			return new(rows, more);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
		{
			throw new TInvestRequestException("T-Invest history record deadline exceeded; retry in 1 s.", TInvestFailureKind.Limit);
		}
		catch (TimeoutException)
		{
			throw new TInvestRequestException("T-Invest history record deadline exceeded; retry in 1 s.", TInvestFailureKind.Limit);
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_stop.Cancel();
		_reservation.Dispose();
		_stop.Dispose();
	}

	static TimeSpan Max(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;

	ISymbolHistoryRecord Bar(TInvestCandle c) => new TInvestBar(c.Time.UtcDateTime, c.Open, c.High, c.Low, c.Close, (double)c.Lots * _lot);
}
