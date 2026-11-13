namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Threading;
using System.Threading.Tasks;
using Dtc.Protocol;
using Dtc.Protocol.Private;
using SomeDataProvider.DataStorage.TInvest;
using SomeDataProvider.DtcProtocolServer.Data;

partial class Session
{
	ProviderHelloResponse? _providerNegotiation;

	long ProviderClockSample() => (_timeProvider.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks / 10;

	void ProcessProviderHello(ProviderHelloRequest hello)
	{
		lock (_sessionLock)
		{
			void Reject(string reason) => SendProviderReject(hello.RequestID, hello.Type, reason, "Provider hello could not be accepted.");
			try
			{
				if (hello.ProbeScopeID != null)
				{
					Reject("Unsupported");
					return;
				}
				var provider = _symbolsStoreProvider as StoresProvider;
				var store = provider?.TInvestReady == true ? provider.GetTInvestInformationStore() : null;
				if (hello.Mode == ProviderHelloModes.ClockOnly)
				{
					if (_providerHello == null || _providerNegotiation == null || (_providerNegotiation.Features & ProviderInformationFeatures.ClockRefreshV1) == 0 || store == null)
					{
						Reject("FeatureUnavailable");
						return;
					}
					using var budget = store.ReconcileBudget.BeginRequest(_providerHello.BudgetScopeID, 0);
					if (hello.BudgetScopeID != _providerHello.BudgetScopeID ||
						(_providerHello.Mode == ProviderHelloModes.JoinHistorical && (hello.Source != _providerHello.Source || hello.InstrumentUID != _providerHello.InstrumentUID || hello.UIDEpoch != _providerHello.UIDEpoch)))
					{
						Reject("EpochMismatch");
						return;
					}
					var identityRejection = HelloIdentityRejection(hello, store);
					if (identityRejection != null)
					{
						Reject(identityRejection);
						return;
					}
					SendProviderMessage(_providerNegotiation with
					{
						RequestID = hello.RequestID, Mode = ProviderHelloModes.ClockOnly, Source = hello.Source,
						InstrumentUID = hello.InstrumentUID, UIDEpoch = hello.UIDEpoch, ProviderUtcMicroseconds = ProviderClockSample(),
					});
					return;
				}
				if (_providerHello != null)
				{
					Reject("AlreadyNegotiated");
					return;
				}
				if (hello.Mode == ProviderHelloModes.JoinHistorical)
				{
					if (store == null || !store.SupportsRecentClosedTicks || hello.Source != "t-invest")
					{
						Reject("Unsupported");
						return;
					}
					var identityRejection = HelloIdentityRejection(hello, store);
					if (identityRejection != null)
					{
						Reject(identityRejection);
						return;
					}
				}
				var features = ProviderInformationFeatures.InstrumentContextV1 | ProviderInformationFeatures.TradingCalendarV1 | ProviderInformationFeatures.EmptyDayProofV1;
				if (store?.SupportsRecentClosedTicks == true) features |= ProviderInformationFeatures.RecentClosedTicksV1 | ProviderInformationFeatures.ClockRefreshV1;
				if (store?.SupportsCatalog == true) features |= ProviderInformationFeatures.SymbolCatalogV1;
				_providerHello = hello;
				_providerNegotiation = new ProviderHelloResponse
				{
					RequestID = hello.RequestID, Mode = hello.Mode, SelectedVersion = hello.MinimumVersion <= 1 ? 1 : 0,
					Features = hello.MinimumVersion <= 1 ? features : 0, BudgetScopeID = hello.BudgetScopeID,
					Source = hello.Source, InstrumentUID = hello.InstrumentUID, UIDEpoch = hello.UIDEpoch,
					ProbeLifetimeSeconds = hello.ProbeLifetimeSeconds, ProviderUtcMicroseconds = ProviderClockSample(),
				};
				SendProviderMessage(_providerNegotiation);
			}
			catch (TInvestRecentRequestException ex) { Reject(ex.Reason); }
			catch (Exception) { Reject("SourceUnavailable"); }
		}
	}

	string? HelloIdentityRejection(ProviderHelloRequest hello, TInvestStore store)
	{
		if (hello.Source != "t-invest" || hello.InstrumentUID == null) return "EpochMismatch";
		var instrument = store.GetCachedContextInstrumentByUid(hello.InstrumentUID);
		if (instrument == null) return "ContextRequired";
		var context = MakeInstrumentContext(instrument, store.Options, instrument.Short.Ticker + "-" + instrument.Short.ClassCode);
		return context.UIDEpoch == hello.UIDEpoch ? null : "EpochMismatch";
	}

	async Task ProcessRecentTicksAsync(RecentTickReconcileRequest request, CancellationToken ct)
	{
		var published = false;
		void Reject(string reason) => SendProviderReject(request.RequestID, request.Type, reason, "The exact recent tick range could not be served.");
		try
		{
			var hello = _providerHello;
			if (hello?.Mode != ProviderHelloModes.JoinHistorical || _providerNegotiation == null || (_providerNegotiation.Features & ProviderInformationFeatures.RecentClosedTicksV1) == 0)
			{
				Reject("FeatureUnavailable");
				return;
			}
			var store = (_symbolsStoreProvider as StoresProvider)?.GetTInvestInformationStore();
			if (store == null)
			{
				Reject("SourceUnavailable");
				return;
			}
			using var budget = store.ReconcileBudget.BeginRequest(hello.BudgetScopeID, request.MaximumSourceAttempts);
			if (hello.BudgetScopeID != request.BudgetScopeID || hello.Source != request.Source || hello.InstrumentUID != request.InstrumentUID || hello.UIDEpoch != request.UIDEpoch)
			{
				Reject("EpochMismatch");
				return;
			}
			var separator = request.Symbol.LastIndexOf('-');
			if (separator <= 0 || separator == request.Symbol.Length - 1 || !ValidCanonicalSymbol(request.Symbol) || request.Symbol.StartsWith("fred", StringComparison.OrdinalIgnoreCase))
			{
				Reject("InvalidRequest");
				return;
			}
			var instrument = store.GetCachedContextInstrument(request.Symbol[..separator], request.Symbol[(separator + 1)..]);
			if (instrument == null)
			{
				Reject("ContextRequired");
				return;
			}
			var context = MakeInstrumentContext(instrument, store.Options, request.Symbol);
			if (context.UIDEpoch != request.UIDEpoch || context.InstrumentUID != request.InstrumentUID || context.SourceSemanticsID != request.SourceSemanticsID)
			{
				Reject("EpochMismatch");
				return;
			}
			var from = DateTimeOffset.FromUnixTimeSeconds(request.From);
			var to = DateTimeOffset.FromUnixTimeSeconds(request.To);
			using var reader = await store.CreateRecentTickReaderAsync(new(instrument), from, to, request.MaximumTicks,
				request.MaximumSourceAttempts, budget, _timeProvider.GetUtcNow().AddSeconds(60), ct).ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();
			var current = store.GetCachedContextInstrument(request.Symbol[..separator], request.Symbol[(separator + 1)..]);
			if (current == null)
			{
				Reject("ContextRequired");
				return;
			}
			if (MakeInstrumentContext(current, store.Options, request.Symbol).UIDEpoch != request.UIDEpoch)
			{
				Reject("EpochMismatch");
				return;
			}
			SendProviderMessage(new SourceOperationStatus
			{
				RequestID = request.RequestID, Source = request.Source, Status = 1, ReasonCode = "Complete",
				AcceptedFrom = request.From, AcceptedTo = request.To, InstrumentUID = request.InstrumentUID,
				UIDEpoch = request.UIDEpoch, SourceSemanticsID = request.SourceSemanticsID, BudgetScopeID = request.BudgetScopeID,
				ProviderUtcMicroseconds = ProviderClockSample(),
			});
			published = true;
			await HistoricalPriceDataStreamer.StreamAsync(reader, request.RequestID, HistoricalDataIntervalEnum.INTERVAL_TICK,
				_currentMessageProtocol.MessageEncoderFactory, bytes => SendResponse(bytes), L, ct).ConfigureAwait(false);
		}
		catch (Exception) when (published) { CloseSession(); }
		catch (TInvestRecentRequestException ex) { Reject(ex.Reason); }
		catch (TInvestRequestException ex) { Reject(ex.Kind == TInvestFailureKind.Limit ? "BudgetExhausted" : "SourceUnavailable"); }
		catch (Exception) when (!ct.IsCancellationRequested) { Reject("SourceUnavailable"); }
	}
}
