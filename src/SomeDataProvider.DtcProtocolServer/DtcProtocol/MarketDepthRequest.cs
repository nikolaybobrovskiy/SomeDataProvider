namespace SomeDataProvider.DtcProtocolServer.DtcProtocol;

using Dtc.Protocol;

sealed record MarketDepthRequest(RequestActionEnum RequestAction, uint SymbolId, string Symbol, string Exchange, int NumLevels);
