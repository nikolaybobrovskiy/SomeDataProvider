namespace SomeDataProvider.DataStorage.TInvest.Test;

using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class CandleSourceTests
{
	[TestCase("IncludeWeekend")]
	[TestCase("Exchange")]
	public void SupportedSourcesAreIndependentAtExistingRangeEnds(string source)
	{
		foreach (var dealer in new[] { false, true })
		{
			foreach (var days in new[] { 1, 31 })
			{
				foreach (var ticks in new[] { 1000, 5_000_000 })
					Assert.DoesNotThrow(() => new TInvestOptions { CandleSource = source, IncludeDealerTrades = dealer, MaxTickDaysPerRequest = days, MaxTicksPerRequest = ticks }.Validate());
			}
		}
	}

	[TestCase("")]
	[TestCase("Unknown")]
	[TestCase("exchange")]
	[TestCase(null)]
	public void InvalidSourceFailsAtStartup(string? source)
	{
		var error = Assert.Throws<ArgumentException>(() => new TInvestOptions { CandleSource = source! }.Validate());
		Assert.That(error!.Message, Does.Contain("CandleSource"));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void WeekendSelectionDoesNotChangeDealerFiltering(bool dealer)
	{
		var options = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["CandleSource"] = "IncludeWeekend", ["IncludeDealerTrades"] = dealer.ToString(),
		}).Build().Get<TInvestOptions>()!;
		options.Validate();
		var now = new ManualTimeProvider().GetUtcNow();
		var plan = TInvestCandlePlan.Create(new(ScriptedTInvestGateway.Invent()), 60, now.AddHours(-1).UtcDateTime, now.UtcDateTime, now, options);
		Assert.That(plan.Calls.Single().IncludeWeekend, Is.True);
		Assert.That(plan.Calls.Single().IncludeDealer, Is.EqualTo(dealer));
	}
}
