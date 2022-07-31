namespace SomeDataProvider.DataStorage.Fred
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Net.Http;
	using System.Threading;
	using System.Threading.Tasks;

	using Microsoft.Extensions.Logging;

	using NBLib.Enum;
	using NBLib.HttpClient;
	using NBLib.Logging;

	using Newtonsoft.Json.Linq;

	using SomeDataProvider.DataStorage.Fred.Dto;

	// https://fred.stlouisfed.org/docs/api/fred/
	public sealed class Service : IDisposable
	{
		internal const string DateFormat = "yyyy-MM-dd";
		const string ApiUrl = "https://api.stlouisfed.org/fred/";
		readonly ServiceApiKey _apiKey;

		readonly HttpClient _httpClient;
		readonly bool _disposeClient;
		readonly ILogger<Service> _logger;

		public Service(ServiceApiKey apiKey, ILoggerFactory loggerFactory)
		{
			_apiKey = apiKey;
			_httpClient = new HttpClient { BaseAddress = new Uri(ApiUrl) };
			_disposeClient = true;
			_logger = loggerFactory.CreateLogger<Service>();
		}

		public Service(HttpClient httpClient, ServiceApiKey apiKey, ILoggerFactory loggerFactory)
		{
			_httpClient = httpClient;
			_apiKey = apiKey;
			_logger = loggerFactory.CreateLogger<Service>();
		}

		public async Task<CategoryInfo[]> GetCategoriesAsync(int parentCategoryId = 0, CancellationToken cancellationToken = default)
		{
			return await _logger.LogOperationAsync(async () =>
			{
				var jsonResponse = await _httpClient.GetJsonAsync($"category/children?category_id={parentCategoryId}&api_key={_apiKey}&file_type=json", cancellationToken);
				if (jsonResponse["categories"] is not JArray catsJson) return Array.Empty<CategoryInfo>();
				return catsJson.ToObject<CategoryInfo[]>() ?? Array.Empty<CategoryInfo>();
			}, "GetCategories({parentCategoryId})", parentCategoryId);
		}

		public async Task<SeriesInfo[]> GetCategorySeriesAsync(int categoryId, CancellationToken cancellationToken = default)
		{
			return await _logger.LogOperationAsync(async () =>
			{
				var jsonResponse = await _httpClient.GetJsonAsync($"category/series?category_id={categoryId}&api_key={_apiKey}&file_type=json", cancellationToken);
				if (jsonResponse["seriess"] is not JArray seriesJson) return Array.Empty<SeriesInfo>();
				return seriesJson.ToObject<SeriesInfo[]>() ?? Array.Empty<SeriesInfo>();
			}, "GetCategorySeries({categoryId})", categoryId);
		}

		public async Task<IReadOnlyCollection<CategorizedSeriesInfo>> GetAllSeriesAsync(CancellationToken cancellationToken = default)
		{
			return await _logger.LogOperationAsync(async () => (await GetAllSeriesAsync(null, cancellationToken)).DistinctBy(x => x.Id).ToArray(), "GetAllSeries()");
		}

		public async Task<SeriesInfo?> GetSeriesInfoAsync(string seriesId, CancellationToken cancellationToken = default)
		{
			var jsonResponse = await _httpClient.GetJsonAsync($"series?series_id={seriesId}&api_key={_apiKey}&file_type=json", cancellationToken);
			if (jsonResponse["seriess"] is JArray series && series.Count > 0)
				return series[0].ToObject<SeriesInfo>();
			return null;
		}

		public async Task<ObservationsData?> GetObservationsAsync(
			string seriesId,
			DateTime start = default,
			DateTime end = default,
			DataValueTransformation transformation = DataValueTransformation.NoTransformation,
			int offset = 0,
			int limit = 0,
			CancellationToken cancellationToken = default)
		{
			return await _logger.LogOperationAsync(async () =>
			{
				var unitsQueryParam = string.Empty;
				if (transformation != DataValueTransformation.NoTransformation)
					unitsQueryParam = $"&units={transformation.GetStringValue()}";

				var limitQueryParam = string.Empty;
				if (limit > 0) limitQueryParam = $"&limit={limit}";

				var offsetQueryParam = string.Empty;
				if (offset > 0) offsetQueryParam = $"&offset={offset}";

				var startQueryParam = string.Empty;
				if (start != default) startQueryParam = $"&observation_start={start.ToString(DateFormat, CultureInfo.InvariantCulture)}";

				var endQueryParam = string.Empty;
				if (end != default) endQueryParam = $"&observation_end={end.ToString(DateFormat, CultureInfo.InvariantCulture)}";

				var uri = $"series/observations?series_id={seriesId}&api_key={_apiKey}&file_type=json{unitsQueryParam}{startQueryParam}{endQueryParam}{offsetQueryParam}{limitQueryParam}";
				var jsonResponse = await _httpClient.GetJsonAsync(uri, cancellationToken);
				if (jsonResponse is JObject jObj)
					return jObj.ToObject<ObservationsData>();
				return null;
			}, "GetObservations({seriesId},{start},{end},{transformation},{offset},{limit})", seriesId, start, end, transformation, offset, limit);
		}

		public void Dispose()
		{
			if (_disposeClient)
				_httpClient.Dispose();
		}

		async Task<IReadOnlyCollection<CategorizedSeriesInfo>> GetAllSeriesAsync((int Id, string FullName)? category = null, CancellationToken cancellationToken = default)
		{
			var result = new List<CategorizedSeriesInfo>();
			if (category != null)
			{
				result.AddRange((await GetCategorySeriesAsync(category.Value.Id, cancellationToken)).Select(series => new CategorizedSeriesInfo(series, category.Value.FullName)));
			}
			var subCategories = await GetCategoriesAsync(category?.Id ?? 0, cancellationToken);
			foreach (var subCategory in subCategories)
				result.AddRange(await GetAllSeriesAsync((subCategory.Id, category != null ? $"{category.Value.FullName} | {subCategory.Name}" : subCategory.Name), cancellationToken));
			return result;
		}
	}
}