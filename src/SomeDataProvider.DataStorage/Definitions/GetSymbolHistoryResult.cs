// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Definitions
{
	using System;
	using System.Collections.Generic;

	public sealed class GetSymbolHistoryResult
	{
		public static readonly GetSymbolHistoryResult Empty = new GetSymbolHistoryResult(Array.Empty<SymbolHistoryRecord>(), null);

		public GetSymbolHistoryResult(IReadOnlyCollection<SymbolHistoryRecord> records, string? continuationToken)
		{
			Records = records;
			ContinuationToken = continuationToken;
		}

		public IReadOnlyCollection<SymbolHistoryRecord> Records { get; }

		public string? ContinuationToken { get; }
	}
}