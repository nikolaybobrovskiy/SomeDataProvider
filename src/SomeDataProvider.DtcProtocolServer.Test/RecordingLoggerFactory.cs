// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Linq;

	using Microsoft.Extensions.Logging;

	/// <summary>A logger factory that keeps every entry, with its structured values, for the test to inspect. Thread-safe.</summary>
	sealed class RecordingLoggerFactory : ILoggerFactory
	{
		readonly ConcurrentQueue<Entry> _entries = new();

		public IReadOnlyList<Entry> Entries => _entries.ToArray();

		/// <summary>Called with each entry right after it is recorded, on the thread that wrote it.</summary>
		public Action<Entry>? Logged { get; set; }

		public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

		public void AddProvider(ILoggerProvider provider)
		{
		}

		public void Dispose()
		{
		}

		/// <summary>Entries written with the given message template, such as "Connected to server: {remoteEndPoint}, {sessionId}".</summary>
		public IReadOnlyList<Entry> WithTemplate(string template) => Entries.Where(e => e.Template == template).ToList();

		public sealed record Entry(string Category, LogLevel Level, string? Template, IReadOnlyDictionary<string, object?> Values, string Message, Exception? Exception);

		sealed class Logger : ILogger
		{
			readonly RecordingLoggerFactory _factory;
			readonly string _category;

			public Logger(RecordingLoggerFactory factory, string category)
			{
				_factory = factory;
				_category = category;
			}

			public IDisposable? BeginScope<TState>(TState state)
				where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
				var values = new Dictionary<string, object?>();
				if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
				{
					foreach (var (key, value) in pairs)
						values[key] = value;
				}
				values.TryGetValue("{OriginalFormat}", out var template);
				var entry = new Entry(_category, logLevel, template as string, values, formatter(state, exception), exception);
				_factory._entries.Enqueue(entry);
				_factory.Logged?.Invoke(entry);
			}
		}
	}
}
