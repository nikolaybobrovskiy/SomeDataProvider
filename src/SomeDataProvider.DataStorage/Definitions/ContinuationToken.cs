namespace SomeDataProvider.DataStorage.Definitions
{
	using System;

	using NBLib.BuiltInTypes;

	public sealed class ContinuationToken : StringValue<ContinuationToken>
	{
		public static explicit operator ContinuationToken(string str)
		{
			return new ContinuationToken { Value = str ?? string.Empty };
		}

		public static explicit operator ContinuationToken(Guid guid)
		{
			return new ContinuationToken { Value = guid.ToString() };
		}
	}
}