namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using SomeDataProvider.DataStorage.TInvest;

/// <summary>Wraps all configured sinks, including structured properties and exception rendering.</summary>
sealed class TInvestRedactingSink : ILogEventSink, IDisposable
{
	static readonly Regex TokenShape = new(@"t\.[A-Za-z0-9_-]{40,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	readonly ILogger _target;
	readonly TInvestToken? _token;

	public TInvestRedactingSink(ILogger target, TInvestToken? token)
	{
		_target = target;
		_token = token;
	}

	public string Redact(string text) => TokenShape.Replace(_token?.Redact(text) ?? text, "<redacted>");

	public void Emit(LogEvent logEvent)
	{
		// Freeze the rendered message before sending it to a sink. No original exception or object survives.
		var message = Redact(logEvent.RenderMessage(CultureInfo.InvariantCulture));
		var template = new MessageTemplateParser().Parse(message.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
		var properties = logEvent.Properties.Select(p => new LogEventProperty(Redact(p.Key), Clean(p.Value))).GroupBy(p => p.Name).Select(g => g.First());
		var exception = logEvent.Exception == null ? null : new Exception(Redact(logEvent.Exception.ToString()));
		_target.Write(new LogEvent(logEvent.Timestamp, logEvent.Level, exception, template, properties));
	}

	public void Dispose() => (_target as IDisposable)?.Dispose();

	LogEventPropertyValue Clean(LogEventPropertyValue value) => value switch
	{
		ScalarValue { Value: string text } => new ScalarValue(Redact(text)),
		ScalarValue { Value: null } => value,
		ScalarValue { Value: bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or TimeSpan or Guid } => value,
		ScalarValue scalar => new ScalarValue(Redact(Convert.ToString(scalar.Value, CultureInfo.InvariantCulture) ?? string.Empty)),
		SequenceValue sequence => new SequenceValue(sequence.Elements.Select(Clean)),
		StructureValue structure => new StructureValue(structure.Properties.Select(p => new LogEventProperty(Redact(p.Name), Clean(p.Value))), structure.TypeTag == null ? null : Redact(structure.TypeTag)),
		DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(p => new KeyValuePair<ScalarValue, LogEventPropertyValue>((ScalarValue)Clean(p.Key), Clean(p.Value)))),
		_ => new ScalarValue(Redact(value.ToString())),
	};
}
