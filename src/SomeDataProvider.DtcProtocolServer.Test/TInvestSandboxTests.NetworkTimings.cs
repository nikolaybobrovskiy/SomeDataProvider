namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using NUnit.Framework;

public partial class TInvestSandboxTests
{
	[Test]
	public void StartupTimingCollectorKeepsOnlyDurationsAndIncompleteSteps()
	{
		using var timings = new SandboxNetworkTimings(false);
		var activity = Guid.NewGuid();
		timings.Record("System.Net.NameResolution", "ResolutionStart", activity, 2);
		timings.Record("System.Net.NameResolution", "ResolutionStop", activity, 7);
		timings.Record("System.Net.Sockets", "ConnectStart", activity, 8);
		timings.Record("System.Net.Sockets", "ConnectStop", activity, 12);
		timings.Record("System.Net.Security", "HandshakeStart", activity, 13);
		timings.Record("System.Net.Security", "HandshakeStop", activity, 50);
		timings.Record("System.Net.Http", "ResponseHeadersStop", activity, 80);
		Assert.That(timings.Summary(100), Does.Contain("dns_ms=5").And.Contain("connect_ms=4").And.Contain("tls_ms=37").And.Contain("first_answer_ms=80"));
		timings.Record("System.Net.Sockets", "ConnectStart", activity, 90);
		Assert.That(timings.Summary(100), Does.Contain("connect_pending_ms=10"));
	}

	// Never inspect EventWrittenEventArgs.Payload: it can contain URIs, headers or other values.
	sealed class SandboxNetworkTimings : EventListener
	{
		readonly object _gate = new();
		readonly Stopwatch _clock = Stopwatch.StartNew();
		readonly Dictionary<(string Kind, Guid Activity), long> _starts = new();
		readonly Dictionary<string, List<long>> _durations = new();
		long? _firstAnswer;
		bool _enabled;

		public SandboxNetworkTimings(bool enabled = true)
		{
			_enabled = enabled;
			if (enabled)
			{
				foreach (var source in EventSource.GetSources())
				{
					Enable(source);
				}
			}
		}

		public void Record(string source, string name, Guid activity, long now)
		{
			lock (_gate)
			{
				if (source == "System.Net.Http" && name == "ResponseHeadersStop")
				{
					_firstAnswer ??= now;
				}

				var kind = source switch
				{
					"System.Net.NameResolution" => "dns",
					"System.Net.Sockets" => "connect",
					"System.Net.Security" => "tls",
					_ => null,
				};
				if (kind == null)
				{
					return;
				}

				if (name is "ResolutionStart" or "ConnectStart" or "HandshakeStart")
				{
					_starts[(kind, activity)] = now;
				}
				else if (name is "ResolutionStop" or "ConnectStop" or "HandshakeStop")
				{
					if (_starts.Remove((kind, activity), out var start))
					{
						if (!_durations.TryGetValue(kind, out var values))
						{
							_durations[kind] = values = new List<long>();
						}

						values.Add(now - start);
					}
				}
			}
		}

		public string Summary(long? elapsed = null)
		{
			lock (_gate)
			{
				var now = elapsed ?? _clock.ElapsedMilliseconds;
				return string.Join(" ", new[] { "dns", "connect", "tls" }.Select(kind =>
					kind + "_ms=" + (_durations.TryGetValue(kind, out var values) ? string.Join(",", values) : "none") + " " +
					kind + "_pending_ms=" + (string.Join(",", _starts.Where(p => p.Key.Kind == kind).Select(p => now - p.Value)) is { Length: > 0 } pending ? pending : "none"))) +
					" first_answer_ms=" + (_firstAnswer?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none") + " startup_total_ms=" + now;
			}
		}

		protected override void OnEventSourceCreated(EventSource eventSource)
		{
			if (_enabled)
			{
				Enable(eventSource);
			}
		}

		protected override void OnEventWritten(EventWrittenEventArgs eventData)
		{
			if (_clock != null)
			{
				Record(eventData.EventSource.Name, eventData.EventName ?? string.Empty, eventData.ActivityId, _clock.ElapsedMilliseconds);
			}
		}

		void Enable(EventSource source)
		{
			if (source.Name is "System.Net.NameResolution" or "System.Net.Sockets" or "System.Net.Security" or "System.Net.Http")
			{
				EnableEvents(source, EventLevel.Informational, EventKeywords.All);
			}
		}
	}
}
