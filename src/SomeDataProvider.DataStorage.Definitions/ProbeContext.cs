namespace SomeDataProvider.DataStorage.Definitions;

/// <summary>Execution-context adapter for source boundaries. Enter explicitly at a diagnostic entry point.</summary>
public static class ProbeContext
{
	static readonly AsyncLocal<State?> CurrentState = new();
	public static ProbeScope? Current => CurrentState.Value?.Scope;
	public static bool IsProbe => CurrentState.Value != null;

	public static IDisposable Enter(ProbeScope? scope)
	{
		var lease = new Lease(CurrentState.Value);
		CurrentState.Value = new(scope);
		return lease;
	}

	public static void Admit(ProbeSource source)
	{
		if (CurrentState.Value is not { } state) return;
		if (state.Scope == null) throw new ProbeRejectedException();
		state.Scope.Admit(source);
	}

	sealed record State(ProbeScope? Scope);
	sealed class Lease(State? previous) : IDisposable
	{
		public void Dispose() => CurrentState.Value = previous;
	}
}
