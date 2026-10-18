// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Threading;
using System.Threading.Tasks;

using Dtc.Protocol;

using Microsoft.Extensions.Logging;

using NBLib.Exceptions;
using NBLib.Logging;

using SomeDataProvider.DataStorage.Definitions;
using SomeDataProvider.DtcProtocolServer.DtcProtocol;

partial class Session
{
	/// <summary>
	/// The most symbol search results FRED is asked to return at once. FRED's own "limit" accepts 1-1000, but a
	/// client-facing search box needs far fewer; this cap is not configurable (PLAN.md §7.3 item 7).
	/// </summary>
	internal const int MaxSymbolSearchResults = 50;

	// SEARCH_TYPE_UNSET behaves like SEARCH_TYPE_BY_DESCRIPTION (FRED's full-text search), the more useful default
	// for a client that does not say which kind of search it wants.
	static SymbolSearchType ToSymbolSearchType(SearchTypeEnum searchType)
	{
		return searchType == SearchTypeEnum.SEARCH_TYPE_BY_SYMBOL ? SymbolSearchType.BySymbol : SymbolSearchType.ByDescription;
	}

	async ValueTask ProcessSymbolSearchRequestAsync(IMessageDecoder decoder, IMessageEncoder encoder, CancellationToken ct)
	{
		var request = decoder.DecodeSymbolSearchRequest();
		var requestId = request.RequestId;
		L.LogInformation("SymbolSearchRequest: {symbolSearchRequest}", request);

		// Per DTC's SYMBOL_SEARCH_REQUEST field description, an empty SearchText is rejected, not answered with
		// NoMatches: https://www.sierrachart.com/index.php?page=doc/DTCMessages_SymbolDiscoverySecurityDefinitionsMessages.php
		if (string.IsNullOrEmpty(request.SearchText))
		{
			L.LogInformation("Answer: SecurityDefinitionReject: EmptySearchText");
			encoder.EncodeSecurityDefinitionReject(requestId, "SearchText must not be empty.");
		}
		else if (!string.IsNullOrEmpty(request.Exchange) || request.SecurityType != SecurityTypeEnum.SECURITY_TYPE_UNSET)
		{
			// Per the same field description, the search matches the specified Exchange and SecurityType too, not
			// SearchText alone. Profile v1 symbols never carry an Exchange or a SecurityType other than unset (see
			// SecurityDefinitionForSymbolRequest's own answers), so a request that constrains either cannot match
			// anything; answer NoMatches without calling the store.
			L.LogInformation("Answer: SecurityDefinitionResponse: NoMatches (Exchange or SecurityType constraint; Profile v1 has neither)");
			encoder.EncodeNoSecurityDefinitionsFound(requestId);
		}
		else
		{
			try
			{
				var searchType = ToSymbolSearchType(request.SearchType);
				var matches = await _symbolsStoreProvider.SearchSymbolsAsync(request.SearchText, searchType, MaxSymbolSearchResults, ct);
				if (matches.Count == 0)
				{
					L.LogInformation("Answer: SecurityDefinitionResponse: NoMatches");
					encoder.EncodeNoSecurityDefinitionsFound(requestId);
				}
				else
				{
					L.LogInformation("Answer: SecurityDefinitionResponse x{count}", matches.Count);
					var i = 0;
					foreach (var symbol in matches)
					{
						i++;
						encoder.EncodeSecurityDefinitionResponse(new IMessageEncoder.EncodeSecurityDefinitionResponseArgs(requestId, symbol, i == matches.Count));
					}
				}
			}
			catch (Exception ex) when (!ex.IsExplainedCancellation())
			{
				// SECURITY_DEFINITION_REJECT is reserved for a real failure (FRED unreachable, a FRED API error, or
				// an invalid request), as opposed to the NoMatches response above for a well-formed search that
				// simply found nothing.
				L.LogException(ex, LogLevel.Warning, "Symbol search failed.");
				encoder.EncodeSecurityDefinitionReject(requestId, $"Symbol search failed: {request.SearchText}.");
			}
		}
		SendAsync(encoder.GetEncodedMessage());
	}
}
