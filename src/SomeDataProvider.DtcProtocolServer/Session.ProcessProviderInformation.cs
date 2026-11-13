namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Protocol.Private;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DtcProtocolServer.Data;

partial class Session
{
	ProviderHelloRequest? _providerHello;

	static bool ValidCanonicalSymbol(string value)
	{
		foreach (var c in value) if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) return false;
		return true;
	}

	static byte[] EncodeProviderMessage(ProviderInformationMessage message)
	{
		var buffer = ArrayPool<byte>.Shared.Rent(ushort.MaxValue);
		try
		{
			var count = ProviderInformationCodec.EncodeVls(message, buffer.AsSpan(0, ushort.MaxValue));
			return buffer.AsSpan(0, count).ToArray();
		}
		finally { ArrayPool<byte>.Shared.Return(buffer); }
	}

	async Task ProcessProviderInformationAsync(Memory<byte> frame, CancellationToken ct)
	{
		var operation = BinaryPrimitives.ReadUInt16LittleEndian(frame.Span.Slice(2));
		// Latch diagnostic intent even if the remaining hello is malformed. It must never fall back to user quotas.
		if (operation == ProviderHelloRequest.MessageType && frame.Length >= 24 &&
			(BinaryPrimitives.ReadUInt64LittleEndian(frame.Span.Slice(16)) & 56UL) != 0) _probeRequested = true;
		var requestId = frame.Length >= 12 ? BinaryPrimitives.ReadInt32LittleEndian(frame.Span.Slice(8)) : 0;
		ProviderInformationMessage request;
		try { request = ProviderInformationCodec.DecodeVls(frame.Span); }
		catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
		{
			if (_probeRequested) _probeRefused = true;
			SendProviderReject(requestId, operation, "InvalidRequest", "Invalid Provider Information request.");
			return;
		}

		if (request is ProviderHelloRequest hello)
		{
			ProcessProviderHello(hello);
			return;
		}

		ct.ThrowIfCancellationRequested();
		if (_providerHello == null || _providerHello.MinimumVersion > 1)
		{
			SendProviderReject(requestId, operation, "FeatureUnavailable", "Negotiate Provider Information version 1 first.");
			return;
		}
		try
		{
			if (request is RecentTickReconcileRequest recent)
			{
				await ProcessRecentTicksAsync(recent, ct).ConfigureAwait(false);
				return;
			}
			if (request is SymbolCatalogRequest catalog)
			{
				await ProcessSymbolCatalogAsync(catalog, ct).ConfigureAwait(false);
				return;
			}
			if (request is InstrumentContextRequest context)
			{
				if (context.Symbol.StartsWith("fred-", StringComparison.Ordinal))
				{
					SendProviderReject(requestId, operation, "FeatureUnavailable", "Context for this source is not available.");
					return;
				}
				var separator = context.Symbol.LastIndexOf('-');
				if (separator <= 0 || separator == context.Symbol.Length - 1 || !ValidCanonicalSymbol(context.Symbol))
				{
					SendProviderReject(requestId, operation, "InvalidRequest", "Use a canonical T-Invest symbol and empty exchange.");
					return;
				}
				var store = (_symbolsStoreProvider as StoresProvider)?.GetTInvestInformationStore();
				if (store == null)
				{
					SendProviderReject(requestId, operation, "SourceUnavailable", "T-Invest source is unavailable.");
					return;
				}
				var instrument = await store.GetContextInstrumentAsync(context.Symbol[..separator], context.Symbol[(separator + 1)..], ct).ConfigureAwait(false);
				if (instrument == null)
				{
					SendProviderReject(requestId, operation, "UnknownSymbol", "Instrument was not found.");
					return;
				}
				var response = MakeInstrumentContext(instrument, store.Options, context.Symbol);
				SendProviderMessage(response with { RequestID = requestId });
				return;
			}
			if (request is TradingCalendarRequest calendar)
			{
				await ProcessTradingCalendarAsync(calendar, ct).ConfigureAwait(false);
				return;
			}
			SendProviderReject(requestId, operation, "FeatureUnavailable", "This Provider Information operation is not supported.");
		}
		catch (TInvestRequestException ex)
		{
			var reason = ex.Kind switch
			{
				TInvestFailureKind.Starting => "SourceStarting",
				TInvestFailureKind.Limit => "LimitRejected",
				TInvestFailureKind.Unsupported => "FeatureUnavailable",
				TInvestFailureKind.Configuration => "SourceNotConfigured",
				_ => "SourceUnavailable",
			};
			SendProviderReject(requestId, operation, reason, "T-Invest metadata is unavailable.", ex.RetryAfterSeconds ?? (ex.Kind == TInvestFailureKind.Starting ? 30 : null));
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Never echo source payloads, names or exception messages into the wire diagnostic.
			SendProviderReject(requestId, operation, "SourceUnavailable", "Provider metadata could not be read.");
		}
	}

	async Task ProcessTradingCalendarAsync(TradingCalendarRequest request, CancellationToken ct)
	{
		void Reject(string reason) => SendProviderReject(request.RequestID, request.Type, reason, "A verified calendar for this revision and range is unavailable.");
		var store = (_symbolsStoreProvider as StoresProvider)?.GetTInvestInformationStore();
		if (store == null)
		{
			Reject("SourceUnavailable");
			return;
		}
		var from = DateOnly.ParseExact(request.FromTradeDate.ToString("D8", CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture);
		var to = DateOnly.ParseExact(request.ToTradeDate.ToString("D8", CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture);
		var calendar = await store.GetTradingCalendarAsync(request.CalendarID, request.CalendarRevision, from, to, ct).ConfigureAwait(false);
		if (calendar == null || calendar.Windows.Any(w => w.IsAnnouncedPlan) ||
			(calendar.RuleMetadata is { } rules && (rules.Revision != calendar.Revision || from < rules.CheckedFrom || to > rules.CheckedTo)))
		{
			Reject("CalendarUnavailable");
			return;
		}
		if (calendar.Id != request.CalendarID || calendar.Revision != request.CalendarRevision)
		{
			Reject("CalendarRevisionMismatch");
			return;
		}
		if (calendar.Expires <= _timeProvider.GetUtcNow())
		{
			Reject("CalendarExpired");
			return;
		}
		if (calendar.CoveredFrom > from || calendar.CoveredTo < to)
		{
			Reject("CalendarIncomplete");
			return;
		}
		if (calendar.IncludeDealerTrades != store.Options.IncludeDealerTrades || calendar.CandleSource != store.Options.CandleSource)
		{
			Reject("SourceMismatch");
			return;
		}
		var messages = new List<ProviderInformationMessage>();
		var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
		foreach (var group in calendar.Windows.Where(w => w.TradeDate >= from && w.TradeDate <= to).GroupBy(w => w.TradeDate).OrderBy(g => g.Key))
		{
			var windows = group.ToArray();
			for (var i = 0; i < windows.Length; i++)
			{
				var window = windows[i];
				messages.Add(new TradingCalendarWindow
				{
					RequestID = request.RequestID, CalendarID = calendar.Id, CalendarRevision = calendar.Revision,
					TradeDate = int.Parse(window.TradeDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
					WindowIndex = i, WindowsInDate = windows.Length, IsMain = window.IsMain,
					MoscowFrom = TimeZoneInfo.ConvertTime(window.From, zone).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
					MoscowTo = TimeZoneInfo.ConvertTime(window.To, zone).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
					UtcFromMicroseconds = (window.From - DateTimeOffset.UnixEpoch).Ticks / 10,
					UtcToMicroseconds = (window.To - DateTimeOffset.UnixEpoch).Ticks / 10,
				});
			}
		}
		messages.Add(new TradingCalendarEnd
		{
			RequestID = request.RequestID, CalendarID = calendar.Id, CalendarRevision = calendar.Revision,
			TradeDateCount = to.DayNumber - from.DayNumber + 1, WindowCount = messages.Count, Complete = true,
		});
		// Encode the whole bounded answer before publishing any row: malformed source metadata cannot produce a partial success.
		var frames = messages.Select(EncodeProviderMessage).ToArray();
		ct.ThrowIfCancellationRequested();
		foreach (var frame in frames) SendResponse(frame);
	}

	InstrumentContextResponse MakeInstrumentContext(TInvestInstrument instrument, TInvestOptions options, string symbol, DateTimeOffset? asOf = null)
	{
		static string? Known(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
		static string Number(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
		static int? Date(DateTimeOffset? value) => value is { } date ? int.Parse(date.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : null;
		var kind = instrument.Short.Kind switch
		{
			TInvestInstrumentKind.Share => 1, TInvestInstrumentKind.Bond => 2, TInvestInstrumentKind.Etf => 3,
			TInvestInstrumentKind.Future => 4, TInvestInstrumentKind.Currency => 5,
			TInvestInstrumentKind.StockOption or TInvestInstrumentKind.FuturesOption or TInvestInstrumentKind.IndexOption => 6,
			_ => 0,
		};
		var usable = TInvestStore.Complete(instrument);
		var tickValue = instrument.Short.Kind switch
		{
			TInvestInstrumentKind.Future => instrument.IncrementAmount,
			TInvestInstrumentKind.Bond => instrument.Increment * instrument.Nominal / 100,
			_ => instrument.Increment,
		};
		var calendar = instrument.Calendar;
		if (calendar != null && (calendar.InstrumentUid != instrument.Short.Uid || calendar.Expires <= (asOf ?? _timeProvider.GetUtcNow()) ||
			calendar.IncludeDealerTrades != options.IncludeDealerTrades || calendar.CandleSource != options.CandleSource)) calendar = null;
		var dailyMode = instrument.DailyStampMode is 1 or 2 ? instrument.DailyStampMode : 0;
		var semantics = $"t-invest:v1;candles={options.CandleSource};candle-filter={(options.IncludeDealerTrades ? "all-returned" : "exchange")};trades={(options.IncludeDealerTrades ? "exchange+dealer" : "exchange")};quantity=units;daily={dailyMode};weekend=unknown;price={instrument.PriceUnit ?? "unknown"}";
		var result = new InstrumentContextResponse
		{
			Symbol = symbol, Source = "t-invest", SourceSemanticsID = semantics,
			// Options expose definitions only; daily ingestion is not authorized by this stage for any kind.
			Kind = kind, Features = usable ? (kind == 6 ? 64UL : 111UL) : 0,
			DailyStampMode = dailyMode, InstrumentUID = Known(instrument.Short.Uid),
			Board = Known(instrument.Board), BoardName = Known(instrument.BoardName), PriceUnit = Known(instrument.PriceUnit),
			CalendarID = calendar?.Id, CalendarRevision = calendar?.Revision,
			Lot = instrument.Short.Lot > 0 ? instrument.Short.Lot : null,
			Currency = Known(instrument.Currency)?.ToUpperInvariant(),
			ExactIncrement = instrument.Increment > 0 ? Number(instrument.Increment) : null,
			MonetaryTickValue = tickValue > 0 ? Number(tickValue) : null,
			FirstMinuteDate = Date(instrument.FirstMinuteCandle), FirstDailyDate = Date(instrument.FirstDailyCandle),
			MaxTickDaysPerRequest = usable ? options.MaxTickDaysPerRequest : null,
			MaxTicksPerRequest = usable ? options.MaxTicksPerRequest : null,
			Description = Known(instrument.Short.Name),
		};
		// Content addressing is stable across sockets/restarts and changes with every exposed definition/semantics field.
		var epoch = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Context = result, instrument.Short.Kind, instrument.Nominal, instrument.BasicAssetSize, instrument.Expiration, options.Environment })));
		return result with { UIDEpoch = epoch };
	}

	void SendProviderReject(int requestId, int operation, string reason, string text, int? retryAfterSeconds = null) =>
		SendProviderMessage(new ProviderRequestReject { RequestID = requestId, FailedOperation = operation, ReasonCode = reason, Text = text, RetryAfterSeconds = retryAfterSeconds });

	void SendProviderMessage(ProviderInformationMessage message) => SendResponse(EncodeProviderMessage(message));
}
