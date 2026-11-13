# T-Bank instrument catalog

An explicit Provider Information v1 hello enables `SymbolCatalogV1` when the ready T-Invest gateway supports bulk lists. Ordinary clients do not need this extension. Neither connecting nor sending hello starts catalog work. The provider builds the first snapshot only after an explicit `SymbolCatalogRequest` with `Catalog = t-invest`.

## Hierarchy and pages

- Empty request `NodeID`: one root node, `t-bank`, displayed as **T-Bank**.
- `t-bank`: **Shares**, **Bonds**, **ETFs**, **Futures**, **Currencies**, with IDs `t-bank/shares`, `t-bank/bonds`, `t-bank/etfs`, `t-bank/futures`, `t-bank/currencies`.
- Each kind: actual source board/class-code groups. A board node is its kind ID plus `/` and the URI-escaped board ID. Kind names are not exchanges.
- Each board: canonical `TICKER-CLASSCODE` leaves, empty exchange, full name, kind, UID, board and known currency. Options are not included.

Requests accept **1–500 rows**, default **200**. These fixed wire bounds are not new configuration settings. Every response frame contains one row, with combined page indexes and count. `SymbolCatalogEnd` gives the exact filtered total, snapshot version, next cursor or absence, and consistency **1 (`ProviderSnapshot`)**. Zero is used only for a known empty listing. Unknown nodes reject with `UnknownNode` rather than pretending to be empty.

`Filter` is a case-insensitive ordinal substring match against the full names of all direct children of the requested node and, for leaves, the canonical symbol. Filtering occurs before pagination across the entire node, not only the first page. It does not recursively search other nodes. Row order is stable within a snapshot: canonical identity for leaves, ordinal board ID for board nodes, documented kind order at the root. Duplicate identical canonical rows collapse; conflicting definitions for the same identity fail the refresh. Distinct canonical identities on different boards remain separate even if their ticker or UID is shared.

## Materialization and refresh

The SDK adapter uses the generated **Shares, Bonds, Etfs, Futures, Currencies** list methods with `InstrumentStatus.All`, through the existing shared Instruments limiter and retry policy. There is no FindInstrument search or per-row definition, calendar, candle or trade lookup. Kind-specific bulk fields supply bond nominal and futures tick value/basic-asset size/expiry. API-tradeability is not a live-data permission and does not truncate browsing.

The cache is store/environment scoped, held in memory and immutable. All five lists and the complete hierarchy must succeed before a new snapshot is published atomically. `ProviderSnapshot` describes this materialized provider view, not an upstream transactional snapshot across the five calls. Fixed safeguards refuse more than **100,000 input rows per kind** or **250,000 total input rows**, never truncate them. Identity/name strings must fit the existing wire bounds; invalid or conflicting source metadata fails materialization. These safeguards are not tunable settings.

After the first request, background refresh runs every **one hour**, using a single shared load and a **60-second total deadline**, including limiter admission/retries. Readers may continue using the previous complete snapshot while a replacement loads. A failed refresh retains those old immutable data but marks the catalog stale: new requests receive **`CatalogStale`**, not an empty/partial success. A single refresh retries after **one minute**; concurrent readers do not create new source work. Store disposal cancels the load and removes its timer. Cancelling an individual reader does not cancel another reader's shared materialization. No catalog polling begins for clients that never ask.

Cursors are opaque authenticated tokens bound to **catalog/source, environment/store, node, exact filter, page size and snapshot version**, plus an offset. They contain no credential and are not persisted across server restarts. Changing any binding rejects with `InvalidCursor`; a superseded snapshot rejects with `SnapshotExpired` (including an old cursor sent without `SnapshotVersion`). An explicitly supplied stale snapshot version also rejects. A new complete refresh gets a new version even when its content is unchanged. Restart browsing without cursor/snapshot after either rejection.

## Context hints and compatibility

Dtc.Protocol, Dtc.Client and Dtc.Conformance pin exactly **8.0.0-alpha.5** from the local feed. Optional entry `Features` and `UIDEpoch` use that package's presence bits. For complete bulk context data, both are computed through the same context builder as `InstrumentContextResponse`, evaluated at the snapshot creation time. Calendar expiry is evaluated at that captured time, so an already materialized row's hints do not drift while paging. An incomplete definition carries neither hint. Unknown is absent, not a fabricated zero mask or epoch. No exact lookup is used to fill them.

Hints label a row and help detect a stale cached context; they **never authorize data requests** or replace selected-item context/definition hydration. Older readers ignore them. Existing standard definition/search/history/market requests and their caps are unchanged. The [FRED category catalog](fred-catalog.md) instead uses best-effort live offsets and never carries these entry hints. Desktop browsing UI and live inventory/release checks are separate tasks. The offline synthetic tests make no claim about measured live inventory counts or publication completeness.
