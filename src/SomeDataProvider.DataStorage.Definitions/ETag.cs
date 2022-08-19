namespace SomeDataProvider.DataStorage.Definitions
{
	using System.Diagnostics.CodeAnalysis;

	using NBLib.BuiltInTypes;

	public sealed class ETag : StringValue<ETag>
	{
		public static readonly ETag Empty = new ETag();

		public static explicit operator ETag([AllowNull] string str)
		{
			return new ETag { Value = str ?? Empty };
		}

		public bool IsOutdated(ETag newValue)
		{
			return IsEmpty || newValue.IsEmpty || this != newValue;
		}
	}
}