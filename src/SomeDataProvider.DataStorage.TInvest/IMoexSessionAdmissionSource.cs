namespace SomeDataProvider.DataStorage.TInvest;

public interface IMoexSessionAdmissionSource
{
	/// <summary>Session is main, morning, evening or weekend. Evidence covers dated lifecycle and mode restrictions too.</summary>
	MoexSessionAdmission GetAdmission(TInvestInstrument instrument, DateOnly civilDate, string session);
}

/// <summary>Proven denial is not missing evidence. A current availability flag is not dated admission.</summary>
public sealed record MoexSessionAdmission(bool Proven, bool Admitted, string Provenance);
