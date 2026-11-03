namespace SomeDataProvider.DataStorage.TInvest;

using Google.Protobuf.WellKnownTypes;
using Tinkoff.InvestApi.V1;

public sealed partial class SdkTInvestGateway
{
	readonly object _archivesLock = new();
	TradeArchiveClient? _archives;
	Func<TradeArchiveClient>? _createArchives;

	public Task<TInvestRateLimiter.Reservation> ReserveArchivesAsync(int downloads, DateTimeOffset deadline, CancellationToken cancellationToken) =>
		_limiter.ReserveAsync(TInvestLimitGroup.Archives, downloads, deadline, cancellationToken);

	public Task<bool> DownloadTradeArchiveAsync(string instrumentId, DateOnly day, string destination, TInvestRateLimiter.Reservation? reservation, DateTimeOffset deadline, CancellationToken cancellationToken) =>
		ArchiveClient().DownloadAsync(instrumentId, day, destination, reservation, deadline, cancellationToken);

	public async Task<IReadOnlyList<TInvestTrade>> GetLastTradesAsync(string uid, DateTimeOffset from, DateTimeOffset to, TInvestRateLimiter.Reservation reservation, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		var response = await CallAsync(TInvestLimitGroup.MarketData, () => _marketData.GetLastTradesAsync(new GetLastTradesRequest
		{
			InstrumentId = uid,
			From = Timestamp.FromDateTimeOffset(from),
			To = Timestamp.FromDateTimeOffset(to),
			TradeSource = TradeSourceType.TradeSourceExchange,
		}, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken, reservation: reservation).ConfigureAwait(false);
		return response.Trades.Select(t => new TInvestTrade(uid, new(t.Time.Seconds, t.Time.Nanos), (double)Decimal(t.Price), t.Quantity,
			t.Direction == TradeDirection.Buy ? TInvestDirection.Buy : t.Direction == TradeDirection.Sell ? TInvestDirection.Sell : TInvestDirection.Unknown,
			t.TradeSource == TradeSourceType.TradeSourceExchange)).ToArray();
	}

	internal void UseArchiveClient(Func<TradeArchiveClient> create) => _createArchives = create;

	// The archive host has no gRPC channel: it is plain HTTPS GET, with the same bundled-root-only trust.
	TradeArchiveClient ArchiveClient()
	{
		lock (_archivesLock)
		{
			return _archives ??= _createArchives?.Invoke() ?? throw new InvalidOperationException("Trade archives are not available on this gateway.");
		}
	}
}
