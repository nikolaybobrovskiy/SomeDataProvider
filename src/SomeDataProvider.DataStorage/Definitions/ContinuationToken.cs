namespace SomeDataProvider.DataStorage.Definitions
{
	using NBLib.BuiltInTypes;

	public sealed class ContinuationToken : StringValue<ContinuationToken>
	{
		public static explicit operator ContinuationToken(string str)
		{
			return new ContinuationToken { Value = str ?? string.Empty };
		}
	}
}