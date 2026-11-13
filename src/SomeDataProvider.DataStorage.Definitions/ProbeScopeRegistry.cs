namespace SomeDataProvider.DataStorage.Definitions;

/// <summary>Process-local scopes, shared by listeners that use this registry.</summary>
public sealed class ProbeScopeRegistry(TimeProvider clock) : IDisposable
{
	readonly object _gate = new();
	readonly Dictionary<string, ProbeScope> _scopes = new(StringComparer.Ordinal);
	readonly Dictionary<string, long> _retired = new(StringComparer.Ordinal);
	bool _disposed;

	public ProbeScope Open(string id, int lifetimeSeconds = 180, int tInvestAttempts = 0, int fredAttempts = 0, bool join = false)
	{
		if (string.IsNullOrWhiteSpace(id) || System.Text.Encoding.UTF8.GetByteCount(id) > 256) throw new ArgumentException("Probe ID must be nonempty and at most 256 UTF-8 bytes.", nameof(id));
		if (lifetimeSeconds is < 30 or > 900) throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds), "Probe lifetime must be 30 through 900 seconds.");
		if (tInvestAttempts is < 0 or > 60) throw new ArgumentOutOfRangeException(nameof(tInvestAttempts), "Probe T-Invest ceiling must be 0 through 60 attempts.");
		if (fredAttempts is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(fredAttempts), "Probe FRED ceiling must be 0 through 6 attempts.");
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			foreach (var retired in _retired.Where(p => clock.GetElapsedTime(p.Value) >= TimeSpan.FromSeconds(900)).Select(p => p.Key).ToArray()) _retired.Remove(retired);
			if (_retired.ContainsKey(id)) throw new ProbeRejectedException();
			if (_scopes.TryGetValue(id, out var scope))
			{
				scope.Check();
				if (scope.LifetimeSeconds != lifetimeSeconds || scope.TInvestCeiling != tInvestAttempts || scope.FredCeiling != fredAttempts) throw new ProbeRejectedException();
				return scope;
			}
			if (join) throw new ProbeRejectedException();
			// Existing sockets retain stopped scope objects, never a renewed budget.
			foreach (var expired in _scopes.Where(p => p.Value.Expired).Select(p => p.Key).ToArray())
			{
				if (_retired.Count >= 1024) throw new ProbeRejectedException();
				_retired[expired] = clock.GetTimestamp();
				_scopes[expired].Dispose();
				_scopes.Remove(expired);
			}
			if (_scopes.Count >= 64) throw new ProbeRejectedException();
			_scopes[id] = scope = new(clock, lifetimeSeconds, tInvestAttempts, fredAttempts);
			return scope;
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			foreach (var scope in _scopes.Values) scope.Dispose();
			_scopes.Clear();
			_retired.Clear();
		}
	}
}
