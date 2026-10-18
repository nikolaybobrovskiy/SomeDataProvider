// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Test
{
	using System;
	using System.Net;
	using System.Net.Http;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>An HttpMessageHandler that answers every request with a canned response; no test using it calls FRED.</summary>
	sealed class FakeFredHandler : HttpMessageHandler
	{
		readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

		public FakeFredHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
		{
			_respond = respond;
		}

		public Uri? LastRequestUri { get; private set; }

		public static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
		{
			return new HttpResponseMessage(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
		}

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			LastRequestUri = request.RequestUri;
			return Task.FromResult(_respond(request));
		}
	}
}
