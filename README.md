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

## Build from source
`SomeDataProvider.DtcProtocolServer` uses `Dtc.Protocol`, the DTC message library of the author's MrktPltfrm
project, which is not on nuget.org. `nuget.config` restores it only from the local folder feed
`I:\Projects\LocalNuGet`, and the project pins its exact version. Before the first build, pack that version of
`Dtc.Protocol` and add it to the feed with `dotnet nuget push <nupkg> -s I:\Projects\LocalNuGet`. `nuget.config`
explains how to use another folder.