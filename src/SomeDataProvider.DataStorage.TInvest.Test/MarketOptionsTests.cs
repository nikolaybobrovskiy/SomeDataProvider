namespace SomeDataProvider.DataStorage.TInvest.Test;

using Microsoft.Extensions.Configuration;
using NUnit.Framework;

[TestFixture]
public class MarketOptionsTests
{
	[TestCase(1)]
	[TestCase(32)]
	public void MaxStreamsRange(int value) => Assert.DoesNotThrow(() => new TInvestOptions
	{
		MaxStreams = value
	}.Validate());
	[TestCase(0)]
	[TestCase(33)]
	public void MaxStreamsRejects(int value) => Assert.That(Assert.Throws<ArgumentException>(() => new TInvestOptions
	{
		MaxStreams = value
	}.Validate())!.Message, Does.Contain("MaxStreams"));
	[TestCase(30)]
	[TestCase(900)]
	public void SilenceRange(int seconds) => Assert.DoesNotThrow(() => new TInvestOptions
	{
		StreamSilenceTimeout = TimeSpan.FromSeconds(seconds)
	}.Validate());
	[TestCase(29)]
	[TestCase(901)]
	public void SilenceRejects(int seconds) => Assert.That(Assert.Throws<ArgumentException>(() => new TInvestOptions
	{
		StreamSilenceTimeout = TimeSpan.FromSeconds(seconds)
	}.Validate())!.Message, Does.Contain("StreamSilenceTimeout"));
	[TestCase(128)]
	[TestCase(100000)]
	public void QueueRange(int value) => Assert.DoesNotThrow(() => new TInvestOptions
	{
		SessionQueueLimit = value
	}.Validate());
	[TestCase(127)]
	[TestCase(100001)]
	public void QueueRejects(int value) => Assert.That(Assert.Throws<ArgumentException>(() => new TInvestOptions
	{
		SessionQueueLimit = value
	}.Validate())!.Message, Does.Contain("SessionQueueLimit"));
	[TestCase(false)]
	[TestCase(true)]
	public void DealerRange(bool value) => Assert.DoesNotThrow(() => new TInvestOptions
	{
		IncludeDealerTrades = value
	}.Validate());
	[TestCase("IncludeDealerTrades", "sometimes")]
	[TestCase("MaxStreams", "1.5")]
	[TestCase("SessionQueueLimit", "many")]
	[TestCase("StreamSilenceTimeout", "infinite")]
	public void InvalidConfigurationRejectsBeforeStartup(string name, string value)
	{
		var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			[name] = value
		}).Build();
		Assert.Throws<InvalidOperationException>(() => config.Get<TInvestOptions>());
	}

	[Test]
	public void WatchListRangeAndCombinationsOfEnds()
	{
		foreach (var streams in new[]
		{
			1,
			32
		}

		)
		{
			foreach (var silence in new[]
			{
				30,
				900
			}

			)
			{
				foreach (var queue in new[]
				{
					128,
					100000
				}

				)
				{
					foreach (var dealer in new[]
					{
						false,
						true
					}

					)
					{
						foreach (var full in new[]
						{
							false,
							true
						}

						)
						{
							var symbols = full ? Enumerable.Range(0, 150 * streams).Select(i => "INVENTED" + i + "-TESTCC").ToArray() : Array.Empty<string>();
							Assert.DoesNotThrow(() => new TInvestOptions
							{
								MaxStreams = streams,
								StreamSilenceTimeout = TimeSpan.FromSeconds(silence),
								SessionQueueLimit = queue,
								IncludeDealerTrades = dealer,
								RecordSymbols = symbols
							}.Validate());
						}
					}
				}
			}
		}

		Assert.DoesNotThrow(() => new TInvestOptions
		{
			RecordSymbols = new[]
			{
				new string('X', 126) + "-Q"
			}
		}.Validate());
	}

	[TestCase("NOEXCHANGE")]
	[TestCase("-TESTCC")]
	[TestCase("INVENTED-")]
	[TestCase("fred-INVENTED")]
	[TestCase("INVENTED\t-TESTCC")]
	[TestCase("")]
	public void InvalidWatchListEntryRejects(string value) => Assert.That(Assert.Throws<ArgumentException>(() => new TInvestOptions
	{
		RecordSymbols = new[]
		{
			value
		}
	}.Validate())!.Message, Does.Contain("RecordSymbols"));
	[Test]
	public void WatchListRejectsDuplicatesNullAndCapacity()
	{
		foreach (var symbols in new[]
		{
			null!,
			new[]
			{
				"INVENTED-TESTCC",
				"INVENTED-TESTCC"
			},
			new[]
			{
				new string('X', 127) + "-Q"
			},
			Enumerable.Range(0, 151).Select(i => "INVENTED" + i + "-TESTCC").ToArray()
		}

		)
		{
			Assert.Throws<ArgumentException>(() => new TInvestOptions
			{
				MaxStreams = 1,
				RecordSymbols = symbols
			}.Validate());
		}
	}
}
