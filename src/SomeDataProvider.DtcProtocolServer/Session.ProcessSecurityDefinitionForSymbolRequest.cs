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
		try
		{
			var route = await _symbolsStoreProvider.GetSymbolsStoreAsync(securityDefinitionRequest.Symbol, securityDefinitionRequest.Exchange, ct);
			var symbol = route == null ? null : await route.Value.SymbolsStore.GetSymbolAsync(route.Value.SymbolCode, ct);
			if (symbol == null)
			{
				encoder.EncodeSecurityDefinitionReject(requestId, "Symbol is unknown or T-Invest source is not configured.");
			}
			else
			{
				encoder.EncodeSecurityDefinitionResponse(new IMessageEncoder.EncodeSecurityDefinitionResponseArgs(requestId, symbol, true)
				{
					Symbol = securityDefinitionRequest.Symbol,
					Exchange = symbol.DataService == SomeDataProvider.DataStorage.Definitions.DataService.TInvest ? securityDefinitionRequest.Exchange : symbol.Exchange,
				});
			}
		}
		catch (SomeDataProvider.DataStorage.TInvest.TInvestRequestException ex)
		{
			encoder.EncodeSecurityDefinitionReject(requestId, ex.Message);
		}
		SendResponse(encoder.GetEncodedMessage());
	}
}