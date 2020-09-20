namespace SomeDataProvider.DataStorage.Fred
{
	using System;
	using System.Globalization;
	using System.Net.Http;
	using System.Threading;
	using System.Threading.Tasks;

	using NBLib.Enum;
	using NBLib.HttpClient;

	using Newtonsoft.Json.Linq;

	// https://fred.stlouisfed.org/docs/api/fred/
	public class Service : IDisposable
	{
		internal const string DateFormat = "yyyy-MM-dd";
		const string ApiUrl = "https://api.stlouisfed.org/fred/";
		readonly ServiceApiKey _apiKey;

		readonly HttpClient _httpClient;
		readonly bool _disposeClient;

		public Service(ServiceApiKey apiKey)
		{
			_apiKey = apiKey;
			_httpClient = new HttpClient { BaseAddress = new Uri(ApiUrl) };
			_disposeClient = true;
		}

		public Service(HttpClient httpClient, ServiceApiKey apiKey)
		{
			_httpClient = httpClient;
			_apiKey = apiKey;
		}

		public async Task<SeriesInfo?> GetSeriesInfoAsync(string seriesId, CancellationToken cancellationToken = default)
		{
			var jsonResponse = await _httpClient.GetJsonAsync($"series?series_id={seriesId}&api_key={_apiKey}&file_type=json", cancellationToken);
			if (jsonResponse["seriess"] is JArray series && series.Count > 0)
			{
				return series[0].ToObject<SeriesInfo>();
			}
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
			var unitsQueryParam = string.Empty;
			if (transformation != DataValueTransformation.NoTransformation)
			{
				unitsQueryParam = $"&units={transformation.GetStringValue()}";
			}

			var limitQueryParam = string.Empty;
			if (limit > 0)
			{
				limitQueryParam = $"&limit={limit}";
			}

			var offsetQueryParam = string.Empty;
			if (offset > 0)
			{
				offsetQueryParam = $"&offset={offset}";
			}

			var startQueryParam = string.Empty;
			if (start != default)
			{
				startQueryParam = $"&observation_start={start.ToString(DateFormat, CultureInfo.InvariantCulture)}";
			}

			var endQueryParam = string.Empty;
			if (end != default)
			{
				endQueryParam = $"&observation_end={end.ToString(DateFormat, CultureInfo.InvariantCulture)}";
			}

			var uri = $"series/observations?series_id={seriesId}&api_key={_apiKey}&file_type=json{unitsQueryParam}{startQueryParam}{endQueryParam}{offsetQueryParam}{limitQueryParam}";
			var jsonResponse = await _httpClient.GetJsonAsync(uri, cancellationToken);
			if (jsonResponse is JObject jObj)
			{
				return jObj.ToObject<ObservationsData>();
			}
			return null;
		}

		public void Dispose()
		{
			if (_disposeClient)
				_httpClient.Dispose();
		}
	}
}