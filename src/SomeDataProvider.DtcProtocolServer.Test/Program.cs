// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.Test
{
	using System;

	/// <summary>
	/// The test project builds as an executable (Microsoft.NET.Test.Sdk sets that for .NET), so it supplies its own entry
	/// point instead of the generated empty one: <see cref="ImmediateDisconnectTests"/> starts this assembly in a child
	/// process to run a probe scenario. The test host never calls it.
	/// </summary>
	static class Program
	{
		static int Main(string[] args)
		{
			if (args.Length == 2 && args[0] == ImmediateDisconnectProbe.Argument)
				return ImmediateDisconnectProbe.Run(args[1]);
			Console.Error.WriteLine($"Usage: dotnet exec SomeDataProvider.DtcProtocolServer.Test.dll {ImmediateDisconnectProbe.Argument} <scenario>");
			return 2;
		}
	}
}
