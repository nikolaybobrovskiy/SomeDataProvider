// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using SomeDataProvider.DtcProtocolServer.DtcProtocol;

partial class Session
{
	async ValueTask ProcessSecurityDefinitionForSymbolRequestAsync(IMessageDecoder decoder, IMessageEncoder encoder, CancellationToken ct)
	{
		var securityDefinitionRequest = decoder.DecodeSecurityDefinitionForSymbolRequest();
		var requestId = securityDefinitionRequest.RequestId;
		L.LogInformation("SecurityDefinitionForSymbolRequest: {securityDefinitionRequest}", securityDefinitionRequest);
		var symbol = await _symbolsStore.GetSymbolAsync(securityDefinitionRequest.Symbol, ct);
		if (symbol == null)
		{
			L.LogInformation("Answer: SecurityDefinitionReject: NoSymbol");
			encoder.EncodeSecurityDefinitionReject(requestId, $"Symbol is unknown: {securityDefinitionRequest.Symbol}.");
			//// encoder.EncodeNoSecurityDefinitionsFound(requestId);
		}
		else
		{
			L.LogInformation("Answer: SecurityDefinitionResponse");
			encoder.EncodeSecurityDefinitionResponse(new IMessageEncoder.EncodeSecurityDefinitionResponseArgs(requestId, symbol, true));
		}
		SendAsync(encoder.GetEncodedMessage());
	}
}