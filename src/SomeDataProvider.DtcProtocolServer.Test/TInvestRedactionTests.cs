namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SomeDataProvider.DataStorage.TInvest;

[TestFixture]
public class TInvestRedactionTests
{
	[Test]
	public void EverySinkGetsOnlyRedactedMessagesExceptionsAndNestedProperties()
	{
		var configured = "invented-unshaped-secret";
		var shaped = "t." + new string('Q', 48);
		var sink1 = new CapturedSink();
		var sink2 = new CapturedSink();
		using var target = new LoggerConfiguration().WriteTo.Sink(sink1).WriteTo.Sink(sink2).CreateLogger();
		using var redactor = new TInvestRedactingSink(target, new TInvestToken(configured));
		using var logger = new LoggerConfiguration().WriteTo.Sink(redactor).CreateLogger();
		logger.ForContext("Nested", new Dictionary<string, object> { [configured] = new[] { shaped, configured } }, destructureObjects: true)
			.Warning(new InvalidOperationException(configured + " " + shaped), "Request {Text} {Count} {Flag}", shaped + " " + configured, 17, true);
		foreach (var sink in new[] { sink1, sink2 })
		{
			Assert.That(sink.Events.Count, Is.EqualTo(1));
			var entry = sink.Events.Single();
			var captured = entry.RenderMessage(CultureInfo.InvariantCulture) + entry.Exception + string.Join(" ", entry.Properties.Select(p => p.Key + p.Value));
			Assert.That(captured.Contains(configured, StringComparison.Ordinal), Is.False, "Exact token survived redaction.");
			Assert.That(captured.Contains(shaped, StringComparison.Ordinal), Is.False, "Token shape survived redaction.");
			Assert.That(captured, Does.Contain("<redacted>"));
			Assert.That(((ScalarValue)entry.Properties["Count"]).Value, Is.EqualTo(17));
			Assert.That(((ScalarValue)entry.Properties["Flag"]).Value, Is.EqualTo(true));
		}
	}

	[Test]
	public void ShapeFilterWorksWhenSourceIsOff()
	{
		var secret = "t." + new string('z', 40);
		var captured = new CapturedSink();
		using var target = new LoggerConfiguration().WriteTo.Sink(captured).CreateLogger();
		using var redactor = new TInvestRedactingSink(target, null);
		using var logger = new LoggerConfiguration().WriteTo.Sink(redactor).CreateLogger();
		logger.Information("{Value}", secret);
		Assert.That(captured.Events.Single().RenderMessage().Contains(secret, StringComparison.Ordinal), Is.False);
	}

	sealed class CapturedSink : ILogEventSink
	{
		public List<LogEvent> Events { get; } = new();

		public void Emit(LogEvent logEvent) => Events.Add(logEvent);
	}
}
