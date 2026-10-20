// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Fred
{
	using System;
	using System.Globalization;
	using System.Net;

	/// <summary>FRED answered a request with an error, or with a body that cannot be used.</summary>
	/// <remarks>
	/// FRED reports errors with an HTTP status and a JSON body with <c>error_code</c> and <c>error_message</c>
	/// (https://fred.stlouisfed.org/docs/api/fred/errors.html). The message never holds the request's URI, which carries the
	/// API key.
	/// </remarks>
	public sealed class FredApiException : Exception
	{
		public FredApiException()
			: this(HttpStatusCode.InternalServerError, null)
		{
		}

		public FredApiException(string message)
			: this(HttpStatusCode.InternalServerError, message)
		{
		}

		public FredApiException(string message, Exception innerException)
			: this(HttpStatusCode.InternalServerError, message, null, innerException)
		{
		}

		/// <param name="statusCode">The HTTP status of FRED's answer.</param>
		/// <param name="errorMessage">FRED's <c>error_message</c>, or another description of the problem; null when there is none.</param>
		/// <param name="retryAfter">The delay of FRED's <c>Retry-After</c> header, if it sent one.</param>
		/// <param name="innerException">The exception that caused this one, if any.</param>
		public FredApiException(HttpStatusCode statusCode, string? errorMessage, TimeSpan? retryAfter = null, Exception? innerException = null)
			: base(FormatMessage(statusCode, errorMessage), innerException)
		{
			StatusCode = statusCode;
			ErrorMessage = errorMessage;
			RetryAfter = retryAfter;
		}

		/// <summary>The HTTP status of FRED's answer; 200 when the answer was successful but unusable.</summary>
		public HttpStatusCode StatusCode { get; }

		/// <summary>FRED's <c>error_message</c>, or another description of the problem; null when there is none.</summary>
		public string? ErrorMessage { get; }

		/// <summary>The delay of FRED's <c>Retry-After</c> header, if it sent one.</summary>
		public TimeSpan? RetryAfter { get; }

		/// <summary>Whether FRED says that the requested series does not exist, such as "Bad Request.  The series does not exist."</summary>
		public bool IsSeriesNotFound =>
			StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
			&& ErrorMessage?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) == true;

		/// <summary>Whether FRED refuses the provider's API key: a configuration problem of the provider, not of the request.</summary>
		public bool IsApiKeyError => ErrorMessage?.Contains("api_key", StringComparison.OrdinalIgnoreCase) == true;

		static string FormatMessage(HttpStatusCode statusCode, string? errorMessage) =>
			$"FRED request failed ({((int)statusCode).ToString(CultureInfo.InvariantCulture)}): {errorMessage ?? "no error message"}";
	}
}
