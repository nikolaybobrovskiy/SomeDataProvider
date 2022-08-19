namespace SomeDataProvider.DataStorage.Definitions
{
	using System;
	using System.Diagnostics.CodeAnalysis;

	using NBLib.BuiltInTypes;

	public sealed class ContinuationToken : StringValue<ContinuationToken>
	{
		public static explicit operator ContinuationToken([AllowNull] string str)
		{
			return new ContinuationToken { Value = str ?? string.Empty };
		}

		public static explicit operator ContinuationToken(Guid guid)
		{
			return new ContinuationToken { Value = guid.ToString() };
		}
	}
}