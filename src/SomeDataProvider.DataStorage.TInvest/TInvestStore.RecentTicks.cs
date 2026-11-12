namespace SomeDataProvider.DataStorage.TInvest;

public sealed partial class TInvestStore
{
	/// <summary>Prepares only the exact closed source range. Never enters archive planning or starts background work.</summary>
	public async Task<TInvestTickReader> CreateRecentTickReaderAsync(TInvestSymbol symbol, DateTimeOffset from, DateTimeOffset to, int maximumTicks,
		int maximumAttempts, TInvestReconcileBudget.Request request, DateTimeOffset deadline, CancellationToken ct = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (!SupportsRecentClosedTicks) throw TInvestReconcileBudget.Reject("FeatureUnavailable");
		if (!Complete(symbol.Instrument) || !symbol.HasMarketDepth) throw TInvestReconcileBudget.Reject("FeatureUnavailable");
		ReconcileBudget.ValidateWindow(from, to);
		if (maximumTicks is < 1 or > 5_000_000 || maximumAttempts is < 1 or > 2) throw TInvestReconcileBudget.Reject("InvalidRequest");
		var firstDay = DateOnly.FromDateTime(from.UtcDateTime);
		var lastDay = DateOnly.FromDateTime(to.AddTicks(-1).UtcDateTime);
		if (lastDay.DayNumber - firstDay.DayNumber + 1 > _options.MaxTickDaysPerRequest) throw TInvestReconcileBudget.Reject("TickDayCap");
		using var budget = NewBudget(deadline, ct);
		var uid = symbol.Instrument.Short.Uid;
		IReadOnlyList<TInvestTrade> trades;
		try
		{
			trades = await _gateway.GetRecentTradesAsync(uid, from, to, maximumAttempts, () =>
			{
				budget.Token.ThrowIfCancellationRequested();
				request.BeforeDispatch(from, to);
			}, deadline, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
			budget.Token.ThrowIfCancellationRequested();
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_stop.IsCancellationRequested) { throw Limited(); }
		if (request.Attempts == 0) throw TInvestReconcileBudget.Reject("SourceUnavailable");
		var ticks = new List<TInvestTick>();
		var cap = Math.Min(maximumTicks, _options.MaxTicksPerRequest);
		foreach (var trade in trades)
		{
			if (trade.Uid != uid || trade.Time.Nanos is < 0 or >= 1_000_000_000 || !double.IsFinite(trade.Price) || trade.Lots <= 0)
				throw TInvestReconcileBudget.Reject("SourceUnavailable");
			if (!trade.IsExchange || trade.Time.Utc < from || trade.Time.Utc >= to) continue;
			if (ticks.Count >= cap) throw TInvestReconcileBudget.Reject("TickCountCap");
			ticks.Add(new(trade.Time, trade.Price, trade.Lots, trade.Direction));
		}
		// Preserve every source occurrence, including equal twins. UTC dates are internal streaming segments only.
		var segments = ticks.OrderBy(t => t.Time.Seconds).ThenBy(t => t.Time.Nanos)
			.GroupBy(t => DateOnly.FromDateTime(t.Time.Utc.UtcDateTime)).Select(g =>
			{
				var day = g.ToList();
				return (Func<CancellationToken, List<TInvestTick>>)(_ => day);
			}).ToArray();
		return new(segments, symbol.Instrument.Short.Lot, ticks.Count, _clock, new());
	}
}
