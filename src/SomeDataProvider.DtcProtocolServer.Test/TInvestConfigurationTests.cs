namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Data;

[TestFixture]
public class TInvestConfigurationTests
{
	[TestCase("[]", true)]
	[TestCase("[\"SYNTH-TESTCC\"]", true)]
	[TestCase("null", false)]
	[TestCase("[null]", false)]
	[TestCase("[1]", false)]
	[TestCase("\"SYNTH-TESTCC\"", false)]
	public void JsonRecordSymbolsMustBeAnArrayOfStrings(string value, bool valid)
	{
		var path = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "tinvest-config-" + Guid.NewGuid() + ".json");
		File.WriteAllText(path, "{\"StoresOptions\":{\"TInvest\":{\"RecordSymbols\":" + value + "}}}");
		try
		{
			using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(path).Build();
			if (valid)
			{
				Assert.DoesNotThrow(() => TInvestConfiguration.Read(configuration));
			}
			else
			{
				Assert.That(Assert.Throws<ArgumentException>(() => TInvestConfiguration.Read(configuration))!.Message, Does.Contain("RecordSymbols"));
			}
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Test]
	public void MisspelledSettingCannotSilentlyDisableRecording()
	{
		using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["StoresOptions:TInvest:RecordSymbol:0"] = "SYNTH-TESTCC"
		}).Build();
		Assert.Throws<ArgumentException>(() => TInvestConfiguration.Read(configuration));
	}

	[TestCase(1)]
	[TestCase(10)]
	[TestCase(20)]
	[TestCase(30)]
	[TestCase(40)]
	[TestCase(50)]
	public void MaxDepthAcceptsSupportedValues(int value)
	{
		using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["StoresOptions:TInvest:MaxDepth"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture)
		}).Build();
		Assert.That(TInvestConfiguration.Read(configuration).MaxDepth, Is.EqualTo(value));
	}

	[TestCase("not-an-integer")]
	[TestCase("15")]
	public void MaxDepthRejectsInvalidConfiguration(string value)
	{
		using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["StoresOptions:TInvest:MaxDepth"] = value
		}).Build();

		var error = Assert.Catch<Exception>(() => TInvestConfiguration.Read(configuration));
		Assert.That(error, Is.Not.Null);
		Assert.That(error!.ToString(), Does.Contain("MaxDepth"));
	}

	[Test]
	public void TickHistorySettingsAreReadFromConfiguration()
	{
		var path = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "tinvest-config-" + Guid.NewGuid() + ".json");
		File.WriteAllText(path, "{\"StoresOptions\":{\"TInvest\":{\"ArchiveCacheFolder\":null}}}");
		try
		{
			using var nullConfiguration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(path).Build();
			Assert.That(TInvestConfiguration.Read(nullConfiguration).ArchiveCacheFolder, Is.Null);
		}
		finally
		{
			File.Delete(path);
		}

		var folder = Path.Combine(Path.GetTempPath(), "sdp-archives");
		using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["StoresOptions:TInvest:MaxTickDaysPerRequest"] = "7",
			["StoresOptions:TInvest:MaxTicksPerRequest"] = "250000",
			["StoresOptions:TInvest:ArchiveCacheFolder"] = folder,
			["StoresOptions:TInvest:ArchiveCacheMaxBytes"] = "2147483648",
		}).Build();
		var options = TInvestConfiguration.Read(configuration);
		Assert.That((options.MaxTickDaysPerRequest, options.MaxTicksPerRequest, options.ArchiveCacheFolder, options.ArchiveCacheMaxBytes), Is.EqualTo((7, 250_000, folder, 2147483648L)));
	}

	[TestCase("MaxTickDaysPerRequest", "32")]
	[TestCase("MaxTickDaysPerRequest", "0")]
	[TestCase("MaxTicksPerRequest", "999")]
	[TestCase("MaxTicksPerRequest", "5000001")]
	[TestCase("ArchiveCacheFolder", "relative\\folder")]
	[TestCase("ArchiveCacheMaxBytes", "1000")]
	[TestCase("ArchiveCacheMaxBytes", "not-a-number")]
	public void TickHistorySettingsOutsideTheirRangesFailAtStartup(string setting, string value)
	{
		using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["StoresOptions:TInvest:" + setting] = value,
		}).Build();
		Assert.That(Assert.Catch<Exception>(() => TInvestConfiguration.Read(configuration))!.ToString(), Does.Contain(setting));
	}

	[TestCase("MaxStreams", "{\"Value\":8}")]
	[TestCase("MaxStreams", "[8]")]
	[TestCase("MaxStreams", "{}")]
	[TestCase("MaxStreams", "[]")]
	[TestCase("MaxStreams", "null")]
	[TestCase("SessionQueueLimit", "{\"Value\":256}")]
	[TestCase("SessionQueueLimit", "[]")]
	[TestCase("StreamSilenceTimeout", "{\"Seconds\":40}")]
	[TestCase("StreamSilenceTimeout", "[]")]
	[TestCase("IncludeDealerTrades", "[true]")]
	[TestCase("IncludeDealerTrades", "{}")]
	[TestCase("IncludeDealerTrades", "null")]
	public void ScalarSettingsRejectJsonContainersAndNull(string setting, string value)
	{
		var path = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "tinvest-config-" + Guid.NewGuid() + ".json");
		File.WriteAllText(path, "{\"StoresOptions\":{\"TInvest\":{\"" + setting + "\":" + value + "}}}");
		try
		{
			using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(path).Build();
			Assert.That(Assert.Throws<ArgumentException>(() => TInvestConfiguration.Read(configuration))!.Message, Does.Contain(setting));
		}
		finally
		{
			File.Delete(path);
		}
	}
}
