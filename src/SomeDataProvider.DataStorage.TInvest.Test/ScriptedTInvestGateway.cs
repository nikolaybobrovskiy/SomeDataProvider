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
	public string? LastQuery { get; private set; }
	public Func<string, string, CancellationToken, Task<TInvestInstrument?>>? Resolve { get; set; }
	public TInvestAccess Access { get; set; } = new(true, new[] { true });

	public static TInvestInstrument Invent(string ticker = "SYNTH", string classCode = "TESTCC", TInvestInstrumentKind kind = TInvestInstrumentKind.Share) =>
		new(new(ticker, classCode, "invented-uid-" + ticker, "Invented instrument", kind, 10, true), 0.01m, "zzz", 0.25m, 1000m, 5m);

	public Task<TInvestAccess> CheckAccessAsync(DateTimeOffset deadline, CancellationToken cancellationToken) => Task.FromResult(Access);

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

	public void Dispose()
	{
	}
}
