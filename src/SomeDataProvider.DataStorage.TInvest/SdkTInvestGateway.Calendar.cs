namespace SomeDataProvider.DataStorage.TInvest;

using Tinkoff.InvestApi.V1;

public sealed partial class SdkTInvestGateway
{
	readonly MoexCalendarSource _calendars = new();
	readonly Dictionary<string, TInvestInstrument> _calendarInstruments = new(StringComparer.Ordinal);
	readonly object _calendarLock = new();

	public Task<TInvestCalendar?> GetTradingCalendarAsync(string id, string revision, DateOnly from, DateOnly to, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		TInvestInstrument? instrument;
		lock (_calendarLock) _calendarInstruments.TryGetValue(id, out instrument);
		return Task.FromResult(instrument != null && revision == MoexCalendarSource.Revision ? _calendars.GetCalendar(instrument, from, to) : null);
	}

	// Exchange is a broker schedule section, not venue proof. RealExchange.Rts means St Petersburg in SDK 0.6.25.
	internal static MoexMarket CalendarMarket(Instrument instrument) => instrument.RealExchange != RealExchange.Moex ? MoexMarket.Unknown : instrument.InstrumentKind switch
	{
		InstrumentType.Share => MoexMarket.Shares,
		InstrumentType.Etf => MoexMarket.Funds,
		InstrumentType.Bond => MoexMarket.Bonds,
		InstrumentType.Futures => MoexMarket.Derivatives,
		_ => MoexMarket.Unknown, // Currency, metals, options and unknown kinds have no released mapping.
	};

	TInvestInstrument RememberCalendarInstrument(TInvestInstrument instrument)
	{
		var today = DateOnly.FromDateTime(_clock.GetUtcNow().ToOffset(TimeSpan.FromHours(3)).DateTime);
		instrument = instrument with { Calendar = _calendars.GetCalendar(instrument, today, today) };
		lock (_calendarLock)
		{
			foreach (var stale in _calendarInstruments.Where(kv => kv.Value.Short.Ticker == instrument.Short.Ticker && kv.Value.Short.ClassCode == instrument.Short.ClassCode).Select(kv => kv.Key).ToArray())
				_calendarInstruments.Remove(stale);
			if (_calendarInstruments.Count >= 4096) _calendarInstruments.Remove(_calendarInstruments.Keys.First());
			_calendarInstruments[MoexCalendarSource.CalendarId(instrument)] = instrument;
		}
		return instrument;
	}
}
