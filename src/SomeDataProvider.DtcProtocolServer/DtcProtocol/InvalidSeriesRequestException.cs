// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using System;

	/// <summary>
	/// A request of the private series extension cannot be decoded or holds an invalid value, but its RequestID can be read,
	/// so the server can answer it with a reject instead of leaving the client to time out.
	/// </summary>
	sealed class InvalidSeriesRequestException : Exception
	{
		public InvalidSeriesRequestException()
			: this(0, "The series request is invalid.")
		{
		}

		public InvalidSeriesRequestException(string message)
			: this(0, message)
		{
		}

		public InvalidSeriesRequestException(string message, Exception innerException)
			: this(0, message, innerException)
		{
		}

		public InvalidSeriesRequestException(int requestId, string message, Exception? innerException = null)
			: base(message, innerException)
		{
			RequestId = requestId;
		}

		/// <summary>The RequestID of the invalid request.</summary>
		public int RequestId { get; }
	}
}
