// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Fred.Dto
{
	using NBLib.BuiltInTypes;

	public sealed class SeriesInfoId : StringValue<SeriesInfoId>
	{
		public static explicit operator SeriesInfoId(string str)
		{
			return new SeriesInfoId { Value = str };
		}
	}
}