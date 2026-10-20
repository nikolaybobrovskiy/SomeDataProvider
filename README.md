# [DTC protocol](https://www.dtcprotocol.org/) data provider for [Sierra Chart](https://www.sierrachart.com/)
Currently implemented data sources:
- [Fred Economic Data](https://fred.stlouisfed.org/docs/api/fred/)

## Use as [Fred Economic](https://fred.stlouisfed.org/docs/api/fred/) Data proxy
- Download SomeDataProvider.DtcProtocolServer from `Release` section.
- Get a [FRED API Key](https://fred.stlouisfed.org/docs/api/api_key.html).
- Configure the key in `appsettings.Local.json`, next to `appsettings.json` in the
  `SomeDataProvider.DtcProtocolServer` output folder. This file is git-ignored, so the
  real key never lands in the repository. Copy `appsettings.Local.example.json` to
  `appsettings.Local.json` and set `StoresOptions.Fred.ApiKey`:
  ```json
  {
      "StoresOptions": {
          "Fred": {
              "ApiKey": "<your FRED API key>"
          }
      }
  }
  ```
  `appsettings.Local.json` is loaded after `appsettings.json` and overrides it, so the
  empty `ApiKey` placeholder that ships in `appsettings.json` is fine to leave as-is.
- Run SomeDataProvider.DtcProtocolServer as `SomeDataProvider.DtcProtocolServer.exe start`
- In [Sierra Chart](https://www.sierrachart.com/) open `Global Settings` -> `Symbol Settings` and add single symbol with the following configuration:
  - Symbol: `fred-~`
  - Use Pattern Matching: `yes`
  - Category: `FRED`
  - Price Display Format: `0.01`
  - Supports Market Depth: `not supported`
  - Allow Zero Value: `allowed`
  - Generic Sub-Client Server Address: `127.0.0.1:50001`
  - Historical Data Server: `127.0.0.1:50001`
  - Historical Daily Data Source: `DTC historical data server`
  - Historical Intraday Data Source: `DTC historical data server`
  - Real-Time Data Client: `generic sub-clients`
- Now one can add any FRED symbol chart:
  - `Find Symbol` -> `Selected Symbol`: `fred-T5YIFR`\
  ![Screenshot](https://raw.githubusercontent.com/nikolaybobrovskiy/SomeDataProvider/master/blob/ExampleFredSymbolFindDlg.png)
  -  Graph should be display as:\
  ![Screenshot](https://raw.githubusercontent.com/nikolaybobrovskiy/SomeDataProvider/master/blob/ExampleFredSymbolChart.png?raw=true)

## Session lifetime and heartbeats
`LOGOFF` from a client closes its connection immediately, stops its timers, and cancels any running requests,
including history downloads. The server queues no further responses after processing `LOGOFF`.

On the streaming server, a successful `LOGON_REQUEST` enables heartbeats at the requested interval of 1 to 300
seconds. If no complete client message arrives for twice that interval, the server cancels the session's requests
and closes the connection. Any complete message counts as activity, not just `HEARTBEAT`; partial frames do not.
An interval of 0 disables heartbeats in both directions and disables loss detection. Negative intervals are clamped
to 0, and intervals above 300 seconds are clamped to 300. The history-only server never sends heartbeats or runs
loss detection, regardless of the requested interval. Client `LOGOFF` still closes a history-only session.

## Series extension
Both servers, the streaming and the history-only one, implement version 1 of MrktPltfrm's private DTC series extension
(`docs/dtc-series-extension.md` in MrktPltfrm, private Types 20000 to 20006, binary encoding with variable-length
strings only). The server keeps no handshake state: it answers `SERIES_METADATA_REQUEST` and
`SERIES_VINTAGES_REQUEST` whenever they arrive, each with its answer or exactly one `SERIES_REQUEST_REJECT`.
- Symbols are `fred-<SERIES_ID>`, optionally with a units suffix such as `.pch`, with an empty Exchange.
- Metadata comes from FRED's `fred/series`. Units are the base series'; the suffix only sets the units
  transformation.
- Vintages come from FRED's `fred/series/observations` with `output_type=1`: one row per observation and real-time
  period, in ascending observation date and then real-time start, keeping FRED's clipping to the requested real-time
  window. A real-time bound of 0 asks for 1776-07-04 or 9999-12-31; FRED's `.` is a row without a value; `ReleaseTime`
  is 0. Rows go out page by page as FRED returns them: a page's complete observation dates are sent before the next
  page is fetched, and a date whose rows continue on the next page waits for that page.
- A missing series is rejected as an unknown symbol; FRED's rate limit, outages and API key problems as source
  unavailable, with a retry hint of 1 to 3,600 seconds when one is known.

## Build from source
`SomeDataProvider.DtcProtocolServer` uses `Dtc.Protocol`, the DTC message library of the author's MrktPltfrm
project, which is not on nuget.org. `nuget.config` restores it only from the local folder feed
`I:\Projects\LocalNuGet`, and the project pins its exact version. Before the first build, pack that version of
`Dtc.Protocol` and add it to the feed with `dotnet nuget push <nupkg> -s I:\Projects\LocalNuGet`. `nuget.config`
explains how to use another folder.