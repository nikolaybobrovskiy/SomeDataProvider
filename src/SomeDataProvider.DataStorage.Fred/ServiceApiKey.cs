// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Fred
{
	using NBLib.BuiltInTypes;

	public sealed class ServiceApiKey : StringValue<ServiceApiKey>
	{
		public static explicit operator ServiceApiKey(string str)
		{
			return new ServiceApiKey { Value = str };
		}
	}
}