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