namespace SomeDataProvider.DtcProtocolServer.Market;

using SomeDataProvider.DataStorage.TInvest;

abstract record HubEvent;
sealed record HubSnapshot(TInvestMarketSnapshot Snapshot) : HubEvent;
sealed record HubUpdate(TInvestMarketEvent Update) : HubEvent;
sealed record HubDepthSnapshot(TInvestOrderBook Book, int Levels) : HubEvent;
sealed record HubDepthUpdate(TInvestOrderBook Book, int Levels) : HubEvent;
sealed record HubFeedStatus(bool Available) : HubEvent;
