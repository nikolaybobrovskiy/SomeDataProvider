// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using Microsoft.Extensions.Logging;

using SomeDataProvider.DtcProtocolServer.DtcProtocol;
using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

partial class Session
{
	void ProcessLogonRequest(IMessageDecoder decoder, IMessageEncoder encoder)
	{
		var logonRequest = decoder.DecodeLogonRequest();
		L.LogInformation("LogonInfo: {heartbeatIntervalInSeconds}, {clientName}, {hardwareIdentifier}", logonRequest.HeartbeatIntervalInSeconds, logonRequest.ClientName, logonRequest.HardwareIdentifier);
		StartHeartbeatTimer(logonRequest.HeartbeatIntervalInSeconds * 1000);
		L.LogInformation("Answer: LogonSuccess");
		encoder.EncodeLogonResponse(LogonStatusEnum.LogonSuccess, "Logon is successful.", _onlyHistoryServer);
		SendAsync(encoder.GetEncodedMessage());
	}
}