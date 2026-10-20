// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Data;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dtc.Protocol.Private;

using Microsoft.Extensions.Logging;

// The series extension: like GetSymbolsStoreAsync, a "fred-" symbol goes to FRED with the prefix removed.
partial class StoresProvider : ISeriesSource
{
	public Task<SeriesMetadataResponse> GetSeriesMetadataAsync(string symbol, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(symbol);
		_l.LogInformation("GetSeriesMetadata({symbol})", symbol);
		return TryGetFredCode(symbol, out var code)
			? new FredSeriesSource(FredStore).GetSeriesMetadataAsync(code, cancellationToken)
			: Task.FromException<SeriesMetadataResponse>(UnknownSymbol(symbol));
	}

	public async IAsyncEnumerable<IReadOnlyList<SeriesVintage>> GetSeriesVintagesAsync(SeriesVintagesRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		_l.LogInformation("GetSeriesVintages({symbol})", request.Symbol);
		if (!TryGetFredCode(request.Symbol, out var code))
			throw UnknownSymbol(request.Symbol);
		await foreach (var batch in new FredSeriesSource(FredStore).GetSeriesVintagesAsync(request with { Symbol = code }, cancellationToken))
			yield return batch;
	}

	static bool TryGetFredCode(string symbol, out string code)
	{
		var prefix = $"{DataSourceFredPrefix}{DataSourcePrefixSeparator}";
		code = symbol.StartsWith(prefix, StringComparison.Ordinal) ? symbol[prefix.Length..] : string.Empty;
		return code.Length > 0;
	}

	static SeriesSourceException UnknownSymbol(string symbol) =>
		new(SeriesRequestRejectReason.UnknownSymbol, $"Unknown symbol: {symbol}. Symbols are fred-<SERIES_ID>, optionally with a units suffix such as .pch.");
}
