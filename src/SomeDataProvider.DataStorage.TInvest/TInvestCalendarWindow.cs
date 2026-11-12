namespace SomeDataProvider.DataStorage.TInvest;

/// <summary>A published trade-date assignment; UTC intervals are half-open.</summary>
public sealed record TInvestCalendarWindow(DateOnly TradeDate, DateTimeOffset From, DateTimeOffset To, bool IsMain);
