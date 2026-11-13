namespace SomeDataProvider.DataStorage.TInvest;

using Google.Protobuf.WellKnownTypes;
using Tinkoff.InvestApi.V1;

public sealed partial class SdkTInvestGateway
{
	public bool SupportsCatalog => true;

	public async Task<IReadOnlyList<TInvestInstrument>> ListInstrumentsAsync(TInvestInstrumentKind kind, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		var request = new InstrumentsRequest { InstrumentStatus = InstrumentStatus.All };
		TInvestInstrument Map(string ticker, string board, string uid, string name, int lot, bool api, Quotation increment, string currency,
			string figi, Timestamp? minute, Timestamp? daily) => new(new(ticker, board, uid, name, kind, lot, api), Decimal(increment), currency,
			Figi: figi, Board: board, FirstMinuteCandle: minute?.ToDateTimeOffset(), FirstDailyCandle: daily?.ToDateTimeOffset(),
			PriceUnit: kind switch { TInvestInstrumentKind.Future => "points", TInvestInstrumentKind.Bond => "percent-of-nominal", _ => "currency" });
		switch (kind)
		{
			case TInvestInstrumentKind.Share:
			{
				var response = await CallAsync(TInvestLimitGroup.Instruments, () => _instruments.SharesAsync(request, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false);
				return response.Instruments.Select(i => Map(i.Ticker, i.ClassCode, i.Uid, i.Name, i.Lot, i.ApiTradeAvailableFlag, i.MinPriceIncrement, i.Currency, i.Figi, i.First1MinCandleDate, i.First1DayCandleDate)).ToArray();
			}
			case TInvestInstrumentKind.Bond:
			{
				var response = await CallAsync(TInvestLimitGroup.Instruments, () => _instruments.BondsAsync(request, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false);
				return response.Instruments.Select(i => Map(i.Ticker, i.ClassCode, i.Uid, i.Name, i.Lot, i.ApiTradeAvailableFlag, i.MinPriceIncrement, i.Currency, i.Figi, i.First1MinCandleDate, i.First1DayCandleDate) with { Nominal = Money(i.Nominal) }).ToArray();
			}
			case TInvestInstrumentKind.Etf:
			{
				var response = await CallAsync(TInvestLimitGroup.Instruments, () => _instruments.EtfsAsync(request, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false);
				return response.Instruments.Select(i => Map(i.Ticker, i.ClassCode, i.Uid, i.Name, i.Lot, i.ApiTradeAvailableFlag, i.MinPriceIncrement, i.Currency, i.Figi, i.First1MinCandleDate, i.First1DayCandleDate)).ToArray();
			}
			case TInvestInstrumentKind.Future:
			{
				var response = await CallAsync(TInvestLimitGroup.Instruments, () => _instruments.FuturesAsync(request, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false);
				return response.Instruments.Select(i => Map(i.Ticker, i.ClassCode, i.Uid, i.Name, i.Lot, i.ApiTradeAvailableFlag, i.MinPriceIncrement, i.Currency, i.Figi, i.First1MinCandleDate, i.First1DayCandleDate)
					with { IncrementAmount = Decimal(i.MinPriceIncrementAmount), BasicAssetSize = Decimal(i.BasicAssetSize), Expiration = i.ExpirationDate?.ToDateTimeOffset() }).ToArray();
			}
			case TInvestInstrumentKind.Currency:
			{
				var response = await CallAsync(TInvestLimitGroup.Instruments, () => _instruments.CurrenciesAsync(request, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false);
				return response.Instruments.Select(i => Map(i.Ticker, i.ClassCode, i.Uid, i.Name, i.Lot, i.ApiTradeAvailableFlag, i.MinPriceIncrement, i.Currency_, i.Figi, i.First1MinCandleDate, i.First1DayCandleDate)).ToArray();
			}
			default: throw new TInvestRequestException("This kind has no bulk catalog.", TInvestFailureKind.Unsupported);
		}
	}
}
