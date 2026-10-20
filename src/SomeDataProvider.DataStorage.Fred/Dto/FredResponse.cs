// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DataStorage.Fred.Dto
{
	using Newtonsoft.Json;

	/// <summary>The fields that every FRED answer may carry when it reports an error.</summary>
	public abstract class FredResponse
	{
		[JsonProperty("error_code")]
		public int? ErrorCode { get; init; }

		[JsonProperty("error_message")]
		public string? ErrorMessage { get; init; }
	}
}
