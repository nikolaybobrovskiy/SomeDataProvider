// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System;

using Dtc.Protocol;

using Microsoft.Extensions.Logging;

using SomeDataProvider.DtcProtocolServer.DtcProtocol;

partial class Session
{
	void ProcessLogonRequest(IMessageDecoder decoder, IMessageEncoder encoder)
	{
		var logonRequest = decoder.DecodeLogonRequest();
		L.LogInformation("LogonInfo: {heartbeatIntervalInSeconds}, {clientName}, {hardwareIdentifier}", logonRequest.HeartbeatIntervalInSeconds, logonRequest.ClientName, logonRequest.HardwareIdentifier);
		lock (_sessionLock)
		{
			if (_closing) return;
			L.LogInformation("Answer: LogonSuccess");
			encoder.EncodeLogonResponse(LogonStatusEnum.LOGON_SUCCESS, "Logon is successful.", _onlyHistoryServer);
			if (SendResponse(encoder.GetEncodedMessage()))
				StartHeartbeatTimers(Math.Clamp(logonRequest.HeartbeatIntervalInSeconds, 0, 300));
		}
	}
}