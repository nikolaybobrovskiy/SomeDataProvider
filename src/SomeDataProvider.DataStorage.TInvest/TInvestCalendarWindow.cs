namespace SomeDataProvider.DataStorage.TInvest;

/// <summary>A published trade-date assignment; UTC intervals are half-open.</summary>
public sealed record TInvestCalendarWindow(DateOnly TradeDate, DateTimeOffset From, DateTimeOffset To, bool IsMain)
{
	public string? Session { get; init; }
	public string? Provenance { get; init; }
	public bool IsAnnouncedPlan { get; init; }
}
