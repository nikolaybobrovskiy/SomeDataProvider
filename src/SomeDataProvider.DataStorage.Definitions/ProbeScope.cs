namespace SomeDataProvider.DataStorage.Definitions;

public sealed class ProbeScope : IDisposable
{
	readonly object _gate = new();
	readonly TimeProvider _clock;
	readonly long _created;
	readonly CancellationTokenSource _stop = new();
	readonly ITimer _expiry;
	int _tInvest;
	int _fred;
	bool _stopped;

	internal ProbeScope(TimeProvider clock, int lifetimeSeconds, int tInvestAttempts, int fredAttempts)
	{
		_clock = clock;
		_created = clock.GetTimestamp();
		LifetimeSeconds = lifetimeSeconds;
		TInvestCeiling = _tInvest = tInvestAttempts;
		FredCeiling = _fred = fredAttempts;
		Cancellation = _stop.Token;
		_expiry = clock.CreateTimer(_ => Stop(), null, TimeSpan.FromSeconds(lifetimeSeconds), Timeout.InfiniteTimeSpan);
	}

	public int LifetimeSeconds { get; }
	public int TInvestCeiling { get; }
	public int FredCeiling { get; }
	public CancellationToken Cancellation { get; }
	public bool Expired => _clock.GetElapsedTime(_created) >= TimeSpan.FromSeconds(LifetimeSeconds);
	public (int TInvest, int Fred) Remaining
	{
		get { lock (_gate) return (_tInvest, _fred); }
	}

	public void Check()
	{
		lock (_gate)
			if (_stopped || Expired) throw new ProbeRejectedException();
	}

	public void Admit(ProbeSource source)
	{
		lock (_gate)
		{
			if (_stopped || Expired || (source == ProbeSource.TInvest ? _tInvest : _fred) == 0)
			{
				Stop();
				throw new ProbeRejectedException();
			}
			if (source == ProbeSource.TInvest) _tInvest--;
			else _fred--;
		}
	}

	public void Dispose()
	{
		Stop();
		_expiry.Dispose();
	}

	void Stop()
	{
		lock (_gate)
		{
			if (_stopped) return;
			_stopped = true;
			// Teardown callbacks must not execute inline inside admission or registry locks.
			_ = _stop.CancelAsync();
		}
	}
}
