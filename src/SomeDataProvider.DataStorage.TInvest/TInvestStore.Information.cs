namespace SomeDataProvider.DataStorage.TInvest;

public sealed partial class TInvestStore
{
	public async Task<TInvestCalendar?> GetTradingCalendarAsync(string id, string revision, DateOnly from, DateOnly to, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (to.DayNumber - from.DayNumber is < 0 or > 31) throw new ArgumentException("Request 1-32 trade dates.");
		var deadline = _clock.GetUtcNow().AddSeconds(8);
		using var budget = NewBudget(deadline, cancellationToken);
		try
		{
			var calendar = await _gateway.GetTradingCalendarAsync(id, revision, from, to, deadline, budget.Token)
				.WaitAsync(TimeSpan.FromSeconds(8), _clock, budget.Token).ConfigureAwait(false);
			budget.Token.ThrowIfCancellationRequested();
			return calendar;
		}
		catch (TimeoutException) { throw Limited(); }
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested) { throw Limited(); }
	}

	/// <summary>Metadata may be incomplete even when the standard definition cannot be served.</summary>
	public async Task<TInvestInstrument?> GetContextInstrumentAsync(string ticker, string classCode, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		var deadline = _clock.GetUtcNow().AddSeconds(8);
		using var budget = NewBudget(deadline, cancellationToken);
		try
		{
			var instrument = await _gateway.GetInstrumentAsync(ticker, classCode, deadline, budget.Token)
				.WaitAsync(TimeSpan.FromSeconds(8), _clock, budget.Token).ConfigureAwait(false);
			budget.Token.ThrowIfCancellationRequested();
			if (instrument != null && (instrument.Short.Ticker != ticker || instrument.Short.ClassCode != classCode))
				throw new TInvestRequestException("T-Invest returned an inconsistent instrument identity.");
			return instrument;
		}
		catch (TimeoutException) { throw Limited(); }
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested) { throw Limited(); }
	}
}
