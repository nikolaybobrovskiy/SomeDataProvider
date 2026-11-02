namespace SomeDataProvider.DtcProtocolServer.Market;

using System;
using System.Collections.Generic;
using System.Linq;
using Dtc.Protocol;
using SomeDataProvider.DataStorage.TInvest;

internal static class DepthBook
{
	static readonly int[] SupportedDepths = { 1, 10, 20, 30, 40, 50 };

	public static int ClientLevels(int requested, int maxDepth)
	{
		if (requested < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(requested));
		}
		if (!IsSupportedDepth(maxDepth))
		{
			throw new ArgumentOutOfRangeException(nameof(maxDepth));
		}
		return Math.Min(requested == 0 ? 10 : requested, maxDepth);
	}

	public static int UpstreamLevels(int clientLevels, int maxDepth)
	{
		if (clientLevels < 1 || !IsSupportedDepth(maxDepth))
		{
			throw new ArgumentOutOfRangeException(clientLevels < 1 ? nameof(clientLevels) : nameof(maxDepth));
		}

		var required = Math.Min(clientLevels, maxDepth);
		var rounded = SupportedDepths.First(level => level >= required);
		return Math.Min(rounded, maxDepth);
	}

	public static TInvestOrderBook Limit(TInvestOrderBook book, int levels)
	{
		var bids = book.Bids.OrderByDescending(level => level.Price).Take(levels).ToArray();
		var asks = book.Asks.OrderBy(level => level.Price).Take(levels).ToArray();
		return book with { Bids = bids, Asks = asks };
	}

	public static bool IsStructurallyConsistent(TInvestOrderBook book) =>
		Valid(book.Bids) && Valid(book.Asks);

	public static TInvestQuote BestQuote(TInvestOrderBook book) => new(
		book.Uid,
		book.Time,
		book.Bids.FirstOrDefault()?.Price,
		book.Bids.FirstOrDefault()?.Lots,
		book.Asks.FirstOrDefault()?.Price,
		book.Asks.FirstOrDefault()?.Lots,
		book.IsConsistent);

	public static bool IsOlder(TInvestTimestamp candidate, TInvestTimestamp current) =>
		candidate.Seconds < current.Seconds || candidate.Seconds == current.Seconds && candidate.Nanos < current.Nanos;

	public static Change[] Diff(TInvestOrderBook previous, TInvestOrderBook current, int levels)
	{
		var changes = new List<Change>();
		DiffSide(previous.Bids, current.Bids, levels, true, changes);
		DiffSide(previous.Asks, current.Asks, levels, false, changes);
		return changes.ToArray();
	}

	static bool IsSupportedDepth(int depth) => SupportedDepths.Contains(depth);

	static bool Valid(IReadOnlyList<TInvestBookLevel> levels)
	{
		var prices = new HashSet<double>();
		foreach (var level in levels)
		{
			if (!double.IsFinite(level.Price) || level.Lots <= 0 || !prices.Add(level.Price))
			{
				return false;
			}
		}
		return true;
	}

	static void DiffSide(IReadOnlyList<TInvestBookLevel> before, IReadOnlyList<TInvestBookLevel> after, int levels, bool bid, List<Change> changes)
	{
		var side = bid ? AtBidOrAskEnum8.AT_BID_8 : AtBidOrAskEnum8.AT_ASK_8;
		var oldLevels = Sorted(before, levels, bid);
		var newLevels = Sorted(after, levels, bid);
		var oldByPrice = oldLevels.Select((level, index) => (level, index)).ToDictionary(item => item.level.Price);
		var newByPrice = newLevels.Select((level, index) => (level, index)).ToDictionary(item => item.level.Price);

		foreach (var item in oldByPrice.Where(item => !newByPrice.ContainsKey(item.Key)).OrderByDescending(item => item.Value.index))
		{
			changes.Add(new(side, MarketDepthUpdateTypeEnum.MARKET_DEPTH_DELETE_LEVEL, item.Value.level.Price, 0, checked((ushort)(item.Value.index + 1))));
		}

		foreach (var item in newByPrice.OrderBy(item => item.Value.index))
		{
			if (!oldByPrice.TryGetValue(item.Key, out var old))
			{
				changes.Add(new(side, MarketDepthUpdateTypeEnum.MARKET_DEPTH_INSERT_LEVEL, item.Value.level.Price, item.Value.level.Lots, checked((ushort)(item.Value.index + 1))));
			}
			else if (old.level.Lots != item.Value.level.Lots)
			{
				changes.Add(new(side, MarketDepthUpdateTypeEnum.MARKET_DEPTH_UPDATE_LEVEL, item.Value.level.Price, item.Value.level.Lots, checked((ushort)(item.Value.index + 1))));
			}
		}
	}

	static TInvestBookLevel[] Sorted(IReadOnlyList<TInvestBookLevel> levels, int count, bool bid) =>
		(bid ? levels.OrderByDescending(level => level.Price) : levels.OrderBy(level => level.Price)).Take(count).ToArray();

	internal sealed record Change(
		AtBidOrAskEnum8 Side,
		MarketDepthUpdateTypeEnum Action,
		double Price,
		long Lots,
		ushort Level);
}
