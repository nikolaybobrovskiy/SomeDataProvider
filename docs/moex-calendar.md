# MOEX market/session rule calendar

The embedded facts table is `src/SomeDataProvider.DataStorage.TInvest/Calendars/moex-session-rules.json`, revision `moex-rules-20261112-v1`. It contains 71 released rule rows, not the older draft rows: generic market/session keys, inclusive effective civil dates, weekdays, Moscow minute envelopes, owning-date rules and official MOEX source links. It contains no copied notice text, ISS downloads, instrument names, tickers or board/class identifiers.

## Scope and Unknown

Market/session rule coverage is 2025-01-27 through 2026-11-12 for shares, funds, bonds and derivatives, subject to their individual session launches and dated exceptions. The separately labelled 2026-11-13 through 2026-12-31 rows are announced plans as of 2026-11-12, not observed trading. There is no 2027 coverage. The one 2025-01-24 derivatives evening row supplies only the carry-in for owning trade date 2025-01-27; it does not release earlier trade dates.

`MoexCalendarSource` expands contributing civil dates, applies the holiday/working-Saturday/weekend/operational-exception overlay, resolves the next verified market main date, and then groups by owning trade date. It never assumes Saturday belongs to Monday. Before ETS the derivatives evening belongs to the next main date; from 2026-03-23 it belongs to its civil date. The cancelled 2026-03-20 evening is absent. Both pre-ETS main parts have the main marker. Moscow `[start,end)` minute envelopes convert to UTC using the fixed +03:00 offset, without an added second or a promise of continuous executions throughout the envelope.

The whole requested range is Unknown if it contains an unresolved owning date (derivatives 2025-03-26 or 2025-04-22), a date outside 2025-01-27–2026-11-12 (announced plans included), unproven dated instrument admission, or contradictory/over-limit windows. Each answer is at most 32 inclusive trade dates and each trade date at most 16 windows. No truncation is permitted. Currency, precious metals, other venues and unknown instrument kinds remain Unknown.

The two disputed weekdays, 2025-02-24 and 2025-03-10, follow their normal dated rules. MOEX's 2025 annual notice `n73701` closes February 23 and March 8 and applies the standard timetable on other dates. Notices `n77829` and `n77810` describe February 22–23 test sessions, not February 24 production hours. Trading counts are not used to invent clocks or admission.

## Runtime instruments and admission

The SDK adapter maps only documented `Instrument.RealExchange == RealExchange.Moex`, combined with `Instrument.InstrumentKind`: Share → shares, Etf → funds, Bond → bonds and Futures → derivatives. `Exchange` is a broker schedule section and is not used as venue proof. In SDK 0.6.25, `RealExchange.Rts` means St Petersburg, not MOEX derivatives. No real identifier is hard-coded.

**Production calendars stay Unknown until a dated admission source is approved.** `IMoexSessionAdmissionSource` takes the instrument, contributing civil date and main/morning/evening/weekend kind, and returns proven/unproven, admitted/denied and bounded provenance. Evidence must cover historical admission, listing/maturity/coupon restrictions and the selected order-book trading mode. Proven denial can exclude a window; missing evidence cannot silently produce a main-only calendar. Owning-date evidence alone cannot authorise its preceding weekend. A mode-specific bond restriction is not a whole-market closure; the admission source must resolve that instrument's dated mode or leave it unproven.

The production implementation reports admission unproven. SDK 0.6.25 documents no reliable morning/evening instrument-admission field; a current `WeekendFlag` or `ApiTradeAvailableFlag` is no historical proof. Calendar requests make no additional SDK, ISS or MOEX calls. Synthetic tests provide dated admission through this seam; they are not real-instrument validation.

Possible later admission sources include MOEX's dated admission lists (outside this public repository because they name securities) and T-Invest's per-instrument schedule code (current week only, broker scope and possibly off-exchange sessions). Neither has been selected or approved here.

The SDK adapter keeps the existing `ITInvestGateway.GetTradingCalendarAsync` seam. Context advertises `CalendarID` and `CalendarRevision` only for a usable calendar; production therefore continues to omit them. Clients retain the visibly unverified `moscowCalendar` fallback and warning. No daily stamp/equivalence, tape-source rule, proof bit, Q2, Q6/L3 or Q9 gate is enabled by market/session facts.

## Transport boundary

Internal snapshots carry provenance, requested covered range and expiry. `RuleMetadata` binds the checked rule range (2025-01-27–2026-11-12), announced-plan horizon (through 2026-12-31) and announcement cutoff (2026-11-12) to revision `moex-rules-20261112-v1`. Internal rule-generated windows also retain their session kind, source/admission provenance and announced-plan marker. The pinned `Dtc.Protocol` 8.0.0-alpha.5 calendar wire types carry identity/revision, owning date, UTC and Moscow boundaries, main/additional marker and final counts only. Provenance, plan labels, covered range and expiry remain internal and documented against the revision; they are not direct wire fields. Under the approved alpha.5 contract, only checked dates can be sent. Requests including any date after 2026-11-12, even a closed date or an announced replacement weekend, get `CalendarUnavailable`, never plan windows or a `Complete` answer. The internal documentary preview is not the production calendar path; the server also rejects plan-bearing snapshots before sending any frame. A future admission-source task may extend the protocol under separate approval.

## Updating the facts

Add a reviewed MOEX notice with its source URL and a boundary/exception test. Split or replace the affected generic rule's inclusive effective range rather than overlap the same market/session key. Keep dated exceptions and any conditional admission requirements separate. Increment the table revision, update the historical/announced coverage and expiry policy, run table integrity tests, every affected boundary test and the offline suites, then obtain independent review. A new notice does not authorise a new admission source or relax the daily/tape gates. Keep only facts and links in the public table, never copied notice prose or security lists.
