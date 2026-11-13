# Recent closed tick source contract

## Wire and admission

Dtc 8.0.0-alpha.5 supplies Types 20100-20114, including the alpha.4 recent-tick contract.
T-Bank catalog entries may carry optional `Features` and `UIDEpoch` hints; FRED entries never do.
`RecentClosedTicksV1` and `ClockRefreshV1`
are advertised together only when the T-Invest store has an attempt-guarded gateway and
`IncludeDealerTrades=false`. Feature negotiation remains once per socket. First acquire
context explicitly, then use a dedicated socket with hello Mode 1 (JoinHistorical), the
same stable logical-profile `BudgetScopeID`, Source, InstrumentUID and UIDEpoch. Neither
joining nor reconnecting creates a new allowance. Context is retained in a bounded
4,096-instrument cache with an unspecified eviction victim, not a FIFO guarantee; an evicted
context must be acquired explicitly again (ContextRequired, including eviction during preparation). A recent
request never hydrates a definition or a catalog.

Type 20114 uses positive whole Unix seconds `[From,To)`. At admission **and immediately
before every real source attempt**, From must be no older than 58 minutes and To must be
at least 2 seconds behind the provider's current UTC clock. A clock correction or queued
request can therefore be rejected. Bounds are never shortened or made open-ended.

The exact requested range goes to `GetLastTrades`, with explicit exchange trade source.
There is no archive read/download, missing-day or candle request, recorder change or
background fill. Successful source occurrences are kept one for one, including equal
twins; the half-open end is filtered and quantities are multiplied by lot once. UTC dates
are internal segments only. `MaxTickDaysPerRequest` (1-31, default 31) and the smaller of
`MaxTicksPerRequest` (1,000-5,000,000, default 5,000,000) and request MaximumTicks still
apply. For a one-day cap a client must plan a piece ending at midnight; the provider
rejects, rather than truncates, a two-date request. A quiet interval needs no recorder.
A failed source call never proves emptiness.

An accepted answer sends Type 20112 before the standard tick header, records and final.
It declares status Complete for this bounded source snapshot, exact accepted bounds,
source, UID, epoch, semantics, budget scope and a fresh provider clock sample. The normal
DTC empty-answer convention is a NoRecordsToReturn header and no tick/final record.
Any preparation failure is one Type 20111 reject before that proof/header. A failure after
publishing the proof closes the answer socket, never sends a contradictory terminal reject.

## Independent rolling ledgers

`TInvest:LiveReconcileRequestsPerMinute` defaults to **6**, accepts **1-12** inclusive,
and rejects other values at startup. It counts decoded recent requests and Mode 2 clock refreshes
in any rolling 60 seconds. Failed binding/window validation and refused overlapping work spend a request;
a request beyond the ceiling is refused without extending the window. One request per
profile may be in flight. The caller queues fairly; the provider rejects busy work rather
than keeping an unbounded queue. The client coordinator deduplicates chart owners per
instrument and owns that fair queue; independent profiles still share the source limiter.

Actual source attempts have their **own** rolling 60-second ledger, capped at twice that
setting (default 12, maximum 24), as well as each request's MaximumSourceAttempts (1-2).
A permitted quota retry must pass both ledgers after the independent shared source
limiter admits it. Delayed attempts from an old connection generation still count when
new-generation starts have become eligible. Ledger ownership is the store, not a socket;
source/UID/epoch qualify each immutable hello/request binding but do not divide the
profile's allowance. At most 1,024 active/recent scopes are retained; new scopes are refused
at capacity, never evicted to refill a ledger. Idle scopes are removed only after both
windows have expired and no request is in flight. IDs are cooperative profile identities,
not authentication: a client must retain its ID across reconnects and not invent new IDs
to evade the bound. Ordinary explicit history retains its distinct existing limits.

Mode 2 refresh requires a previously negotiated ClockRefreshV1 socket, the same scope and
valid context identity (and the identical binding on a joined socket). It returns the saved
negotiated version, features, limits and scope, with Mode 2, the accepted source/UID/epoch
and a fresh clock sample. A missing cached context is ContextRequired on both join and refresh;
a present changed epoch or a joined socket's different binding is EpochMismatch.
It cannot renegotiate, join a probe or reset either ledger. A failed refresh leaves the
client's evidence held. Recent source preparation has a 60-second provider deadline and
propagates caller cancellation; no timer uses sleeps in the offline fixtures.

## IR11 release gate: not yet satisfied

Documentary check on 2026-11-12 of the publisher's
[official protobuf contract](https://github.com/RussianInvestments/investAPI/blob/3eaf23a25f598fe483c913184acdbd9132bc68d2/src/docs/contracts/marketdata.proto)
(source revision 2025-11-07):

- GetLastTrades describes anonymized instrument trades and guarantees information for
  the last hour. The request defines UTC from/to bounds and explicit trade-source selection.
- GetLastTradesResponse contains only a trade list, with no pagination or publication-
  completeness watermark. This is the documented scope used here, not a universal bound
  on delayed publication or a promise that the two-second cutoff has settled permanently.
- The current developer-site pages
  [method reference](https://developer.tbank.ru/invest/api/market-data-service-get-last-trades)
  and [gRPC contract](https://developer.tbank.ru/invest/services/quotes/marketdata/)
  could not be independently retrieved in this check (untrusted TLS chain). The dated
  official source above is evidence of that published scope, not verification of a newer
  contract or a live source measurement.

**Observed closed-answer lag: unmeasured.** No live source call or L11 run occurred in this
implementation. The two-second cutoff is an admission policy, not a measured source-lag
bound. Synthetic success only establishes the provider/wire/ledger behavior. Advertising
this provider capability is not authorization for a trusted full-v2 settlement release.
The W12 gate must record the separately approved L11 outcome and the documented scope.
If it cannot support the cutoff, trusted full-v2 settlement stays disabled: require an
explicitly approved bounded cutoff/reconfirmation policy within the same quotas, or an
explicitly accepted provisional-only release with retained evidence and visible capacity
stop. An inconclusive finite probe does not establish a universal lag bound. No extra
calls, larger sample or automatic archive repair are authorized by this document.
