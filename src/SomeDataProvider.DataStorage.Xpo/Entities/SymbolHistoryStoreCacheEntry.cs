// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

#pragma warning disable CC0022
namespace SomeDataProvider.DataStorage.Xpo.Entities
{
	using System;
	using System.Collections.Generic;
	using System.Threading;
	using System.Threading.Tasks;

	using DevExpress.Xpo;

	using SomeDataProvider.DataStorage.Definitions;
	using SomeDataProvider.DataStorage.Xpo.Entities.Helpers;

	class SymbolHistoryStoreCacheEntry : XPLiteObject, ISymbolHistoryStoreCacheEntry
	{
		string _symbolCode = string.Empty;
		HistoryInterval _historyInterval;
		DateTime _cachedPeriodStart;
		DateTime _cachedPeriodEnd;
		ETag _eTag = ETag.Empty;
		DateTime _revisablePeriodStart;

		public SymbolHistoryStoreCacheEntry(Session session)
			: base(session)
		{
		}

		[Key(true)]
		public int Id { get; set; }

		[Indexed(nameof(HistoryInterval), Unique = true)]
		public string SymbolCode
		{
			get => _symbolCode;
			set => SetPropertyValue(nameof(SymbolCode), ref _symbolCode, value);
		}

		public HistoryInterval HistoryInterval
		{
			get => _historyInterval;
			set => SetPropertyValue(nameof(HistoryInterval), ref _historyInterval, value);
		}

		public DateTime CachedPeriodStart
		{
			get => _cachedPeriodStart;
			set => SetPropertyValue(nameof(CachedPeriodStart), ref _cachedPeriodStart, value);
		}

		public DateTime CachedPeriodEnd
		{
			get => _cachedPeriodEnd;
			set => SetPropertyValue(nameof(CachedPeriodEnd), ref _cachedPeriodEnd, value);
		}

		[ValueConverter(typeof(ETagValueConverter))]
		public ETag ETag
		{
			get => _eTag;
			set => SetPropertyValue(nameof(ETag), ref _eTag, value);
		}

		public DateTime RevisablePeriodStart
		{
			get => _revisablePeriodStart;
			set => SetPropertyValue(nameof(RevisablePeriodStart), ref _revisablePeriodStart, value);
		}

		internal SymbolHistoryStoreXpoCache? Store { private get; set; }

		public async Task UpdateEtagAsync(ETag eTag, CancellationToken cancellationToken = default)
		{
			using var session = new UnitOfWork(Store!.DataLayer);
			var entry = await session.GetObjectByKeyAsync<SymbolHistoryStoreCacheEntry>(Id, cancellationToken);
			if (entry == null)
			{
				throw new InvalidOperationException($"Entry (Id = {Id}) not found.");
			}
			entry.ETag = eTag;
			await session.CommitChangesAsync(cancellationToken);
		}

		public Task SaveRecordsAsync(IReadOnlyCollection<ISymbolHistoryRecord> records, CancellationToken cancellationToken = default)
		{
			throw new NotImplementedException();
		}
	}
}