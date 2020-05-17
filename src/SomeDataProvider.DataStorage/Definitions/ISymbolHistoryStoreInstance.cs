// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Definitions
{
	using System;

	// It can dispose Store or not dispose if single instance is used.
	public interface ISymbolHistoryStoreInstance : IDisposable
	{
		ISymbolHistoryStore Store { get; }
	}
}