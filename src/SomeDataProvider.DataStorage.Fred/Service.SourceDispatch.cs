namespace SomeDataProvider.DataStorage.Fred;

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBLib.HttpClient;
using Newtonsoft.Json.Linq;
using SomeDataProvider.DataStorage.Definitions;

public sealed partial class Service
{
	// All legacy, catalog and real-time paths dispatch here. There are no automatic HTTP retries.
	Task<HttpResponseMessage> SendSourceAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		ProbeContext.Admit(ProbeSource.Fred);
		return _httpClient.SendAsync(request, completion, ct);
	}

	async Task<JToken> GetSourceJsonAsync(string uri, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, uri);
		using var response = await SendSourceAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
		return await response.GetJsonAsync(ct).ConfigureAwait(false);
	}
}
