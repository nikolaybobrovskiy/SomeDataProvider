// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Definitions
{
	using System;
	using System.Collections.Generic;

	public sealed class SymbolHistoryResponse
	{
		public static readonly SymbolHistoryResponse Empty = new(Array.Empty<ISymbolHistoryRecord>(), default);

		public SymbolHistoryResponse(IReadOnlyCollection<ISymbolHistoryRecord> records, bool hasMore)
		{
			Records = records;
			HasMore = hasMore;
		}

		public IReadOnlyCollection<ISymbolHistoryRecord> Records { get; }

		public bool HasMore { get; }
	}
}