namespace SomeDataProvider.DataStorage.Definitions
{
	using NBLib.BuiltInTypes;

	public sealed class ETag : StringValue<ETag>
	{
		public static readonly ETag Empty = new ETag();

		public static explicit operator ETag(string str)
		{
			return new ETag { Value = str ?? string.Empty };
		}

		public bool IsOutdated(ETag newValue)
		{
			return IsEmpty || newValue.IsEmpty || this != newValue;
		}
	}
}