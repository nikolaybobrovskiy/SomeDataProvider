namespace SomeDataProvider.DataStorage.Fred;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed partial class Service : IFredCatalogGateway
{
	public const int MaxCatalogPageSize = 500;
	public const int MaxCatalogChildren = 10000;
	public const int MaxCatalogResponseBytes = 4 * 1024 * 1024;

	async Task<IReadOnlyList<FredCatalogCategory>> IFredCatalogGateway.GetChildrenAsync(int categoryId, CancellationToken ct)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(categoryId);
		var body = await GetCatalogJsonAsync("category/children?category_id=" + categoryId.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
		if (body["categories"] is not JArray rows || rows.Count > MaxCatalogChildren) throw new FredCatalogException("InvalidSourceResponse");
		return rows.Select(r => new FredCatalogCategory(CatalogInteger(r["id"]), CatalogInteger(r["parent_id"]), CatalogText(r["name"]))).ToArray();
	}

	async Task<FredCatalogSeriesPage> IFredCatalogGateway.GetSeriesPageAsync(int categoryId, int offset, int limit, CancellationToken ct)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(categoryId);
		ArgumentOutOfRangeException.ThrowIfNegative(offset);
		if (limit is < 1 or > MaxCatalogPageSize) throw new ArgumentOutOfRangeException(nameof(limit), "Catalog page size must be 1 through 500.");
		var query = string.Create(CultureInfo.InvariantCulture, $"category/series?category_id={categoryId}&offset={offset}&limit={limit}&order_by=series_id&sort_order=asc");
		var body = await GetCatalogJsonAsync(query, ct).ConfigureAwait(false);
#pragma warning disable CC0021 // These are FRED JSON field names, not C# parameter names.
		if (body["seriess"] is not JArray rows || rows.Count > limit || CatalogInteger(body["offset"]) != offset || CatalogInteger(body["limit"]) != limit)
#pragma warning restore CC0021
			throw new FredCatalogException("InvalidSourceResponse");
		int? total = body["count"] == null || body["count"]!.Type == JTokenType.Null ? null : CatalogInteger(body["count"]);
		if (total is { } count && count < (long)offset + rows.Count && rows.Count != 0) throw new FredCatalogException("InvalidSourceResponse");
		return new FredCatalogSeriesPage(offset, limit, total, rows.Select(r => new FredCatalogSeries(CatalogText(r["id"]), CatalogText(r["title"]))).ToArray());
	}

	static int CatalogInteger(JToken? value)
	{
		if (value?.Type != JTokenType.Integer || !int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var result))
			throw new FredCatalogException("InvalidSourceResponse");
		return result;
	}

	string CatalogText(JToken? value)
	{
		if (value?.Type != JTokenType.String || (string?)value is not { } text || string.IsNullOrWhiteSpace(text) ||
			text.Contains(_apiKey.ToString(), StringComparison.Ordinal)) throw new FredCatalogException("InvalidSourceResponse");
		return text;
	}

	async Task<JObject> GetCatalogJsonAsync(string query, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		try
		{
			// Reuse the existing client and credentials, but never log the URL or source body.
			using var request = new HttpRequestMessage(HttpMethod.Get, query + "&file_type=json&api_key=" + _apiKey);
			using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode) throw new FredCatalogException("SourceUnavailable");
			if (response.Content.Headers.ContentLength > MaxCatalogResponseBytes) throw new FredCatalogException("InvalidSourceResponse");
			await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
			using var bytes = new MemoryStream();
			var buffer = new byte[8192];
			int read;
			while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
			{
				if (bytes.Length + read > MaxCatalogResponseBytes) throw new FredCatalogException("InvalidSourceResponse");
				bytes.Write(buffer, 0, read);
			}
			var body = JObject.Parse(Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length));
			if (body["error_message"] != null || body["error_code"] != null) throw new FredCatalogException("SourceUnavailable");
			return body;
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
		catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
		{
			// Transport/JSON exceptions can quote a credential-bearing URI. Do not retain their messages or inner exceptions.
			throw new FredCatalogException("SourceUnavailable");
		}
	}
}
