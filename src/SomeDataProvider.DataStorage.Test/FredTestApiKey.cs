namespace SomeDataProvider.DataStorage.Test
{
	using System;
	using System.IO;
	using System.Text.Json;

	using NUnit.Framework;

	using SomeDataProvider.DataStorage.Fred;

	// Resolves a FRED API key for tests without hard-coding a secret.
	// Never log, print or otherwise expose the resolved value.
	static class FredTestApiKey
	{
		public const string EnvironmentVariableName = "FRED_API_KEY";

		const string LocalSettingsFileName = "appsettings.Local.json";

		public static ServiceApiKey? TryGet()
		{
			var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName);
			if (!string.IsNullOrEmpty(fromEnvironment)) return (ServiceApiKey)fromEnvironment;

			var fromFile = TryReadFromLocalSettingsFile();
			return !string.IsNullOrEmpty(fromFile) ? (ServiceApiKey)fromFile : null;
		}

		// Returns the configured key, or calls Assert.Ignore (which throws) when none is configured.
		public static ServiceApiKey GetOrSkip()
		{
			var key = TryGet();
			if (key == null)
			{
				Assert.Ignore(
					$"FRED API key is not configured. Set the {EnvironmentVariableName} environment variable, " +
					$"or create {LocalSettingsFileName} next to the test binary " +
					$"(see appsettings.Local.example.json in this project) to run this test.");
			}
			return key!;
		}

		static string? TryReadFromLocalSettingsFile()
		{
			var path = Path.Combine(AppContext.BaseDirectory, LocalSettingsFileName);
			if (!File.Exists(path)) return null;
			using var stream = File.OpenRead(path);
			using var document = JsonDocument.Parse(stream);
			if (document.RootElement.TryGetProperty("StoresOptions", out var storesOptions)
				&& storesOptions.TryGetProperty("Fred", out var fred)
				&& fred.TryGetProperty("ApiKey", out var apiKey)
				&& apiKey.ValueKind == JsonValueKind.String)
			{
				return apiKey.GetString();
			}
			return null;
		}
	}
}
