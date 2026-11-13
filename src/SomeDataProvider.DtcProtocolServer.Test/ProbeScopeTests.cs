namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using NUnit.Framework;
using SomeDataProvider.DataStorage.Definitions;

[TestFixture]
public class ProbeScopeTests
{
	[Test]
	public void PurgingAnExpiredScopeDoesNotRecreateItsBudget()
	{
		var clock = new ManualTimeProvider();
		using var scopes = new ProbeScopeRegistry(clock);
		var old = scopes.Open("synthetic-old", 30, 1, 0);
		clock.Advance(TimeSpan.FromSeconds(30));
		scopes.Open("synthetic-new", 30, 1, 0);
		Assert.Throws<ProbeRejectedException>(() => scopes.Open("synthetic-old", 30, 1, 0));
		Assert.Throws<ProbeRejectedException>(() => old.Admit(ProbeSource.TInvest));
	}

	[Test]
	public void UnknownAndMismatchedJoinsCannotCreateOrRefillABudget()
	{
		using var scopes = new ProbeScopeRegistry(new ManualTimeProvider());
		Assert.Throws<ProbeRejectedException>(() => scopes.Open("synthetic-missing", join: true));
		var scope = scopes.Open("synthetic-probe", 180, 2, 1);
		scope.Admit(ProbeSource.TInvest);
		Assert.Throws<ProbeRejectedException>(() => scopes.Open("synthetic-probe", 900, 2, 1));
		Assert.Throws<ProbeRejectedException>(() => scopes.Open("synthetic-probe", 180, 3, 1));
		Assert.That(scopes.Open("synthetic-probe", 180, 2, 1), Is.SameAs(scope));
		Assert.That(scope.Remaining, Is.EqualTo((1, 1)));
	}

	[Test]
	public void JoiningSocketsShareCountersAndCannotRenewAnExpiredScope()
	{
		var clock = new ManualTimeProvider();
		using var scopes = new ProbeScopeRegistry(clock);
		var scope = scopes.Open("synthetic-probe", 30, 2, 0, false);
		scope.Admit(ProbeSource.TInvest);
		var joined = scopes.Open("synthetic-probe", 30, 2, 0, true);
		Assert.That(joined, Is.SameAs(scope));
		Assert.That(joined.Remaining, Is.EqualTo((1, 0)));
		clock.Advance(TimeSpan.FromSeconds(30));
		Assert.Throws<ProbeRejectedException>(() => scope.Admit(ProbeSource.TInvest));
		Assert.Throws<ProbeRejectedException>(() => scopes.Open("synthetic-probe", 30, 2, 0, true));
	}

	[TestCase(29, 1, 0)]
	[TestCase(901, 1, 0)]
	[TestCase(180, -1, 0)]
	[TestCase(180, 61, 0)]
	[TestCase(180, 1, -1)]
	[TestCase(180, 1, 7)]
	public void InvalidDiagnosticLimitsAreRejected(int lifetime, int tInvest, int fred)
	{
		using var scopes = new ProbeScopeRegistry(new ManualTimeProvider());
		Assert.Throws<ArgumentOutOfRangeException>(() => scopes.Open("synthetic-probe", lifetime, tInvest, fred, false));
	}

	[TestCase(30, 0, 0)]
	[TestCase(30, 60, 6)]
	[TestCase(900, 0, 6)]
	[TestCase(900, 60, 0)]
	public void RangeEndsAreAcceptedWithoutSpendingAnySourceAttempt(int lifetime, int tInvest, int fred)
	{
		using var scopes = new ProbeScopeRegistry(new ManualTimeProvider());
		Assert.That(scopes.Open("synthetic-probe", lifetime, tInvest, fred, false).Remaining, Is.EqualTo((tInvest, fred)));
	}

	[Test]
	public void AProbeWithoutAnEnforceableScopeAdmitsNothing()
	{
		using (ProbeContext.Enter(null)) Assert.Throws<ProbeRejectedException>(() => ProbeContext.Admit(ProbeSource.Fred));
		Assert.DoesNotThrow(() => ProbeContext.Admit(ProbeSource.Fred));
	}

	[Test]
	public void SourceCeilingsAreSeparateAndAnExhaustedProbeDoesNotStopUserWork()
	{
		var clock = new ManualTimeProvider();
		using var scopes = new ProbeScopeRegistry(clock);
		var scope = scopes.Open("synthetic-probe", 180, 1, 2, false);
		using (ProbeContext.Enter(scope))
		{
			ProbeContext.Admit(ProbeSource.TInvest);
			ProbeContext.Admit(ProbeSource.Fred);
			Assert.That(scope.Remaining, Is.EqualTo((0, 1)));
			Assert.Throws<ProbeRejectedException>(() => ProbeContext.Admit(ProbeSource.TInvest));
			Assert.Throws<ProbeRejectedException>(() => ProbeContext.Admit(ProbeSource.Fred), "A refused source admission stops both sources for this finite diagnostic run.");
		}
		Assert.DoesNotThrow(() => ProbeContext.Admit(ProbeSource.TInvest));
	}
}
