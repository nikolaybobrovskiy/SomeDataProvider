// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Xpo.Entities.Helpers
{
	using System;

	using DevExpress.Xpo.Metadata;

	using SomeDataProvider.DataStorage.Definitions;

	class ETagValueConverter : ValueConverter
	{
		public override object ConvertToStorageType(object value)
		{
			return value is ETag etag ? etag.Value : null!;
		}

		public override object ConvertFromStorageType(object value)
		{
			return value is string strVal ? (ETag)strVal : ETag.Empty;
		}

		public override Type StorageType => typeof(string);
	}
}