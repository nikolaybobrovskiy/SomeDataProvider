namespace SomeDataProvider.DataStorage.Fred.Dto;

using System;

public sealed class CategorizedSeriesInfo : SeriesInfo
{
	public CategorizedSeriesInfo(SeriesInfo seriesInfo, string category)
		: base(seriesInfo)
	{
		Category = category ?? throw new ArgumentNullException(nameof(category));
	}

	public string Category { get; }
}