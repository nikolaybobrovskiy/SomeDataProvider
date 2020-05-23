#pragma warning disable 8602
#pragma warning disable CC0022

namespace SomeDataProvider.DataStorage.Test
{
	using System.Threading.Tasks;

	using NBLib.BuiltInTypes;

	using NUnit.Framework;

	using SomeDataProvider.DataStorage.InMem;

	[TestFixture]
	public class SymbolsStoreTests
	{
		[Test]
		public async Task TestGetSymbolAsync()
		{
			using SymbolsStore store = new SymbolsStore();
			var symbol = await store.GetSymbolAsync("fred-RUSCPIALLMINMEI.pc1");
			Assert.IsNotNull(symbol);
			Assert.IsFalse(symbol.Description.IsEmpty());
			Assert.IsTrue(symbol.Description.Contains("% Chg y/y"));
		}
	}
}