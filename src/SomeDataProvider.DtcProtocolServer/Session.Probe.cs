namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Threading;
using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DtcProtocolServer.Data;
using SomeDataProvider.DtcProtocolServer.Market;

partial class Session
{
	TInvestMarketDataHub? _probeHub;
	CancellationTokenRegistration _probeCancellation;

	TInvestMarketDataHub? SessionMarketHub
	{
		get
		{
			if (!_probeRequested) return ((Server)Server).MarketDataHub;
			lock (_sessionLock)
			{
				if (_closing || _probeRefused || _probeScope == null) throw new ProbeRejectedException();
				_probeScope.Check();
				if (_probeHub != null) return _probeHub;
				var store = (_symbolsStoreProvider as StoresProvider)?.GetTInvestInformationStore();
				if (store == null) return null;
				// One immutable owner for the actor, snapshots, resyncs and reconnects. No user interest or recorder is moved.
				using var probeContext = ProbeContext.Enter(_probeScope);
				return _probeHub = new TInvestMarketDataHub(store.Gateway, store.Options, store.Clock, persistRecording: false);
			}
		}
	}

	void AttachProbeScope()
	{
		_probeCancellation = _probeScope!.Cancellation.UnsafeRegister(static state =>
		{
			// CancelAsync must never run socket teardown inside an admission lock.
			ThreadPool.QueueUserWorkItem(static session => ((Session)session!).CloseSession(), state);
		}, this);
	}

	void StopProbeWork()
	{
		_probeCancellation.Unregister();
		_probeHub?.Dispose();
	}
}
