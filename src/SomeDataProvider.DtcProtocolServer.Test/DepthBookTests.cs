namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Generic;
using System.Linq;
using Dtc.Protocol;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DtcProtocolServer.Market;

[TestFixture]
public class DepthBookTests
{
	[TestCase(0, 10, 10)]
	[TestCase(1, 1, 1)]
	[TestCase(7, 10, 7)]
	[TestCase(15, 20, 15)]
	[TestCase(50, 50, 50)]
	[TestCase(100, 50, 50)]
	public void ClientDepthRoundsUpstreamAndLimitsOutput(int requested, int upstream, int output)
	{
		var clientLevels = DepthBook.ClientLevels(requested, 50);
		Assert.That(clientLevels, Is.EqualTo(output));
		Assert.That(DepthBook.UpstreamLevels(clientLevels, 50), Is.EqualTo(upstream));
	}

	[Test]
	public void MaxDepthCapsTheRoundedUpstreamLevelCount()
	{
		var clientLevels = DepthBook.ClientLevels(15, 10);
		Assert.That(clientLevels, Is.EqualTo(10));
		Assert.That(DepthBook.UpstreamLevels(clientLevels, 10), Is.EqualTo(10));
	}

	[Test]
	public void DepthHelpersRejectUnsupportedMaximumDepth()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => DepthBook.ClientLevels(12, 15));
		Assert.Throws<ArgumentOutOfRangeException>(() => DepthBook.UpstreamLevels(12, 15));
	}

	[Test]
	public void StructurallyMalformedBooksAreRejectedBeforeDiffing()
	{
		var valid = new TInvestOrderBook("uid", new(1, 0), 10, new[] { new TInvestBookLevel(100, 2) }, new[] { new TInvestBookLevel(101, 3) });
		Assert.That(DepthBook.IsStructurallyConsistent(valid), Is.True);
		Assert.That(DepthBook.IsStructurallyConsistent(valid with { Bids = new[] { new TInvestBookLevel(100, 2), new TInvestBookLevel(100, 4) } }), Is.False);
		Assert.That(DepthBook.IsStructurallyConsistent(valid with { Asks = new[] { new TInvestBookLevel(101, 0) } }), Is.False);
		Assert.That(DepthBook.IsStructurallyConsistent(valid with { Bids = new[] { new TInvestBookLevel(double.NaN, 2) } }), Is.False);
	}

	[Test]
	public void DiffTransformsRandomBooksIntoTheExactTopNState()
	{
		const int levels = 7;
		var random = new Random(81427);
		for (var iteration = 0; iteration < 500; iteration++)
		{
			var previous = NewBook(random, iteration);
			var current = NewBook(random, iteration + 1_000);
			var previousView = DepthBook.Limit(previous, levels);
			var changes = DepthBook.Diff(previous, current, levels);
			var bids = previousView.Bids.ToDictionary(level => level.Price, level => level.Lots);
			var asks = previousView.Asks.ToDictionary(level => level.Price, level => level.Lots);
			foreach (var change in changes)
			{
				var side = change.Side == AtBidOrAskEnum8.AT_BID_8 ? bids : asks;
				if (change.Action == MarketDepthUpdateTypeEnum.MARKET_DEPTH_DELETE_LEVEL)
				{
					side.Remove(change.Price);
				}
				else
				{
					side[change.Price] = change.Lots;
				}
			}

			Assert.That(bids.OrderByDescending(level => level.Key).Take(levels).Select(level => new TInvestBookLevel(level.Key, level.Value)), Is.EqualTo(Sorted(current.Bids, levels, true)), $"bid state at iteration {iteration}");
			Assert.That(asks.OrderBy(level => level.Key).Take(levels).Select(level => new TInvestBookLevel(level.Key, level.Value)), Is.EqualTo(Sorted(current.Asks, levels, false)), $"ask state at iteration {iteration}");
		}
	}

	static TInvestOrderBook NewBook(Random random, int seed)
	{
		var prices = Enumerable.Range(0, 30).Select(i => 90d + i * 0.25).ToArray();
		var shuffled = prices.OrderBy(_ => random.Next()).ToArray();
		var bidCount = random.Next(0, 15);
		var askCount = random.Next(0, 15);
		var bids = shuffled.Take(bidCount).Select(price => new TInvestBookLevel(price, random.Next(1, 100))).ToArray();
		var asks = shuffled.Skip(15).Take(askCount).Select(price => new TInvestBookLevel(price, random.Next(1, 100))).ToArray();
		return new("uid-" + seed, new(seed, 0), 50, bids, asks);
	}

	static TInvestBookLevel[] Sorted(IReadOnlyList<TInvestBookLevel> book, int levels, bool bid) =>
		(bid ? book.OrderByDescending(level => level.Price) : book.OrderBy(level => level.Price)).Take(levels).ToArray();
}
