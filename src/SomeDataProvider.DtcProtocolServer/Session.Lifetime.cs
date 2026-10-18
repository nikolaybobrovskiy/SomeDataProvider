// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Threading;

using Microsoft.Extensions.Logging;

using NBLib.CodeFlow;
using NBLib.Exceptions;

partial class Session
{
	// All sends and the close transition share this lock. An in-flight request cannot queue bytes after LOGOFF.
	readonly object _sessionLock = new();
	readonly TimeProvider _timeProvider;
	ITimer? _heartbeatTimer;
	ITimer? _lossTimer;
	TimeSpan _lossTimeout;
	long _lastMessageTimestamp;
	bool _closing;

	bool SendResponse(byte[] bytes)
	{
		lock (_sessionLock)
			return !_closing && SendAsync(bytes);
	}

	// Called with _sessionLock held, after the successful logon response was queued.
	void StartHeartbeatTimers(int intervalInSeconds)
	{
		StopHeartbeatTimers();
		if (_closing || _onlyHistoryServer || intervalInSeconds == 0) return;
		var interval = TimeSpan.FromSeconds(intervalInSeconds);
		_lossTimeout = interval + interval;
		_lastMessageTimestamp = _timeProvider.GetTimestamp();
		_lossTimer = _timeProvider.CreateTimer(_ => OnLossTimer(), null, _lossTimeout, Timeout.InfiniteTimeSpan);
		_heartbeatTimer = _timeProvider.CreateTimer(_ => OnHeartbeatTimer(), null, interval, interval);
	}

	void OnHeartbeatTimer()
	{
		lock (_sessionLock)
		{
			if (_closing || _heartbeatTimer == null) return;
			var encoder = _currentMessageProtocol.MessageEncoderFactory.CreateMessageEncoder();
			using (new Finally(() => encoder.TryDispose()))
			{
				encoder.EncodeHeartbeatMessage(0);
				SendResponse(encoder.GetEncodedMessage());
			}
		}
	}

	void OnLossTimer()
	{
		lock (_sessionLock)
		{
			if (_closing || _lossTimer == null) return;
			// A callback can already be queued when a complete message resets the timer.
			var remaining = _lossTimeout - _timeProvider.GetElapsedTime(_lastMessageTimestamp);
			if (remaining > TimeSpan.Zero)
			{
				_lossTimer.Change(remaining, Timeout.InfiniteTimeSpan);
				return;
			}
			BeginClose();
			L.LogInformation("Client messages timed out; closing connection: {remoteEndPoint}", RemoteEndPoint);
		}
		CancelAndDisconnect();
	}

	// Called with _sessionLock held. Disposing a timer does not wait for queued callbacks.
	void StopHeartbeatTimers()
	{
		_heartbeatTimer?.Dispose();
		_heartbeatTimer = null;
		_lossTimer?.Dispose();
		_lossTimer = null;
	}

	bool BeginClose()
	{
		if (_closing) return false;
		_closing = true;
		StopHeartbeatTimers();
		return true;
	}

	void StopSession()
	{
		lock (_sessionLock)
			if (!BeginClose()) return;
		_disconnectTokenSource.Cancel();
	}

	void CloseSession()
	{
		lock (_sessionLock)
			if (!BeginClose()) return;
		CancelAndDisconnect();
	}

	void CancelAndDisconnect()
	{
		try
		{
			_disconnectTokenSource.Cancel();
		}
		finally
		{
			Disconnect();
		}
	}
}
