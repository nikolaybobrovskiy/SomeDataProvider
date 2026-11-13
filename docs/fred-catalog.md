# FRED category catalog

An explicit Provider Information v1 hello advertises `SymbolCatalogV1` when FRED is configured, independently of T-Invest availability. `Catalog = fred` uses Types 20107–20110. Connecting, hello and the local root do not request upstream data. Existing exact FRED lookup, units suffixes, observations and the series extension are unchanged.

## Hierarchy and explicit pages

- Empty `NodeID` returns the local **FRED** root, ID `fred`, with a known total of one (or zero if its name does not match the filter).
- Expanding `fred` reads FRED category 0. A child with synthetic ID 17 has node ID `fred/17`; its child 29 is `fred/17/29`. These path IDs are navigation, not instrument identities.
- A category expansion first reads only `category/children`. Its children are deduplicated by ID, ordered by numeric ID and returned in bounded pages. Ancestor/self edges are omitted. Repeated IDs within a requested path, malformed/noncanonical numeric IDs and paths deeper than 16 nodes reject, never recurse. A displayed node at depth 16 is a fixed traversal boundary: expanding it rejects with `LimitRejected` before any upstream call (including a series call), rather than fetching children that cannot be represented at the next level.
- Series follow on a separate explicitly requested page after the final child page. A category with no children returns its first series page immediately. Each such request calls only `category/series`, once, with its raw offset, requested limit and `order_by=series_id&sort_order=asc`. There is no prefetch, page refill, all-category walk, observation request, description search or per-row definition request.
- A series leaf uses canonical `fred-SYNTHSERIES` with empty exchange and its full source title. Browsing selects base units; existing exact lookup can still select a supported units suffix. A series encountered by multiple paths keeps the same instrument identity.

Requests accept **1–500 rows**, default **200**, using the existing wire contract. Child and series rows use combined page indexes/counts and a common local generation. `Filter` is an ordinal case-insensitive substring of a child name or the current series page's ID/title. Unlike a fully materialized T-Bank catalog, this is a **page-local filter**: a page can have no matches and still have a next cursor. Filtering/deduplication never changes the consumed source offset or induces another source request. Clients must follow `NextCursor`, not infer end from a short or empty output page.

T-Bank entries may carry optional `Features` and `UIDEpoch` hints; FRED entries never do. All three Dtc packages remain pinned exactly to **8.0.0-alpha.5**.

## Best-effort consistency

Every FRED `SymbolCatalogEnd` declares consistency **2 (`BestEffortOffsetView`)**, including an empty page or the local root. `SnapshotVersion` is only a **local cache/view generation**, never an upstream FRED snapshot token.

Expanded categories leave `Total` absent. The upstream count describes raw series only, not the deduplicated, locally filtered combined child/series listing, so it is not presented as an exact total. An absent source count remains unknown, never zero. A known source count is used internally to detect a change and to stop at its current end. Without one, a full raw page permits one next request; a short/empty raw page ends the current listing.

The view deduplicates series identities. A changed observed count or a backward series-ID ordering invalidates the generation with `SnapshotExpired`. A same-count insertion/deletion between pages can remain undetectable: a series may be missed, or an overlap may be suppressed. The end marker therefore means only **“End of current listing; source may change — Refresh”**, never that every source series was seen. Replayed cached pages are not revalidated with the source, so unobserved content changes are not claimed to have been detected.

Cursors are opaque, random, store-local tokens bound to catalog/source, full category path, exact filter, page size and local generation. A token cannot supply an arbitrary offset. Changed bindings or tampering reject with `InvalidCursor`; an expired, evicted or invalidated generation rejects with `SnapshotExpired`, including when the request omits `SnapshotVersion`. Refresh starts without cursor or version. Restarting the provider also expires these in-memory tokens.

## Fixed safeguards and lifetime

These are fixed implementation limits, **not new configuration settings**:

| Bound | Value and disposition |
|---|---|
| View lifetime | 10 minutes on the injected monotonic clock; checked before source work and before caching/publication |
| Concurrent retained views | 16; the oldest generation is evicted as a whole |
| Pages per generation | 128; another page rejects with `LimitRejected` before any upstream call; use normal 200/500-row pages rather than tiny pages for large categories |
| Retained row-weight budget | 50,000 across all views, counting child metadata, seen identities and cached rows/end markers; evict whole other views first, otherwise reject explicitly |
| Category children | 10,000 per explicit response; excess rejects, never truncates |
| Category path | At most 16 nodes including category 0; expansion at depth 16 rejects before source dispatch |
| Upstream JSON body | 4 MiB, enforced for declared and streamed length |
| Names/identity | Existing wire limits: 1,024 UTF-8 bytes for names; 64 bytes including the `fred-` symbol prefix. Invalid/overlong metadata rejects without truncating identity |

The provider shares one serialized cache across sockets, with no timer or background catalog request. A fresh category may make one children call and one bounded series-page call; later pages make zero or one source call. Concurrent readers reuse an already cached answer. Failed/canceled loads discard partially changed offsets and deduplication state; no partial page is sent. Source errors and transport exceptions become safe diagnostics without echoing credentials, request URLs or response bodies. The HTTP client and credentials are the existing FRED service's; no new retry policy or package is added.

An older/unnegotiated bridge client gets no unsolicited catalog frames and induces no catalog call. Clients without this extension keep exact FRED lookup and show **“FRED categories require a newer SomeDataProvider.”** The desktop UI and separately approved live checks are outside this change.

## Offline evidence

`FredCatalogTests` uses a fake `IFredCatalogGateway` and `ManualTimeProvider`: 5,000/20,000 rows at 200/500 per page; same-count insertion/deletion; observable count/order changes; ancestor cycles and duplicate paths/rows; unknown total through current end; expiry/eviction; cursor binding/tampering; page-local filtering and exact raw offsets; hard page admission; concurrency; cancellation/failure cleanup. Wire tests exercise the negotiated FRED-only path, bounded HTTP requests, cached replay, absent totals and absent context hints. `FredCatalogGatewayTests` checks the real adapter against a fake HTTP handler, including malformed responses, range rejection, cancellation, response-size limits and credential-safe failures. None calls FRED or T-Invest.
