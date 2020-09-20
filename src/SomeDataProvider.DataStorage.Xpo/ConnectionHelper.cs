namespace SomeDataProvider.DataStorage.Xpo
{
	using System;

	using DevExpress.Xpo;
	using DevExpress.Xpo.DB;
	using DevExpress.Xpo.Metadata;

	using SomeDataProvider.DataStorage.Xpo.Entities;

	public static class ConnectionHelper
	{
		static readonly Type[] PersistentTypes =
		{
			typeof(SymbolHistoryStoreCacheEntry),
		};

		public static IDataLayer CreateDataLayer(string connectionString, bool threadSafe = true)
		{
			var reflectionDictionary = new ReflectionDictionary();
			reflectionDictionary.GetDataStoreSchema(PersistentTypes);
			var autoCreateOption = AutoCreateOption.DatabaseAndSchema;
			var provider = XpoDefault.GetConnectionProvider(connectionString, autoCreateOption);
			return threadSafe ? (IDataLayer)new ThreadSafeDataLayer(reflectionDictionary, provider) : new SimpleDataLayer(reflectionDictionary, provider);
		}
	}
}