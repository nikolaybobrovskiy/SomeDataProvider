namespace SomeDataProvider.DtcProtocolServer.Data;

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using SomeDataProvider.DataStorage.TInvest;

static class TInvestConfiguration
{
	public static TInvestOptions Read(IConfiguration configuration)
	{
		var section = configuration.GetSection("StoresOptions:TInvest");
		if (section.Value != null)
		{
			throw new ArgumentException("TInvest settings must be an object.");
		}

		var names = typeof(TInvestOptions).GetProperties().Where(p => p.SetMethod != null).Select(p => p.Name).Append("Token").ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (section.GetChildren().Any(c => !names.Contains(c.Key)))
		{
			throw new ArgumentException("Unknown TInvest setting; check the setting names.");
		}

		foreach (var setting in section.GetChildren().Where(c => !string.Equals(c.Key, "RecordSymbols", StringComparison.OrdinalIgnoreCase)))
		{
			var nullAllowed = string.Equals(setting.Key, "ArchiveCacheFolder", StringComparison.OrdinalIgnoreCase);
			if ((setting.Value == null && !nullAllowed) || setting.GetChildren().Any())
			{
				throw new ArgumentException("TInvest:" + setting.Key + " must be a scalar value, not null or a container.");
			}
		}

		var records = section.GetSection("RecordSymbols");
		if (!string.IsNullOrEmpty(records.Value) || records.GetChildren().Select((c, index) => c.Key != index.ToString(CultureInfo.InvariantCulture) || c.Value == null || c.GetChildren().Any()).Any(invalid => invalid))
		{
			throw new ArgumentException("TInvest:RecordSymbols must be an array of strings.");
		}

		// The provider's null/empty sentinels lose JSON shape. Inspect startup files before accepting them.
		var fileArray = false;
		if (configuration is IConfigurationRoot root)
		{
			foreach (var provider in root.Providers.OfType<JsonConfigurationProvider>())
			{
				var info = provider.Source.FileProvider?.GetFileInfo(provider.Source.Path!);
				if (info?.Exists != true)
				{
					continue;
				}

				using var stream = info.CreateReadStream();
				using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
				{
					CommentHandling = JsonCommentHandling.Skip,
					AllowTrailingCommas = true
				});
				var stores = Property(document.RootElement, "StoresOptions");
				var invest = stores.HasValue ? Property(stores.Value, "TInvest") : null;
				if (invest.HasValue)
				{
					if (invest.Value.ValueKind != JsonValueKind.Object)
					{
						throw new ArgumentException("TInvest settings must be an object.");
					}

					foreach (var setting in invest.Value.EnumerateObject().Where(p => !string.Equals(p.Name, "RecordSymbols", StringComparison.OrdinalIgnoreCase)))
					{
						var nullOk = string.Equals(setting.Name, "ArchiveCacheFolder", StringComparison.OrdinalIgnoreCase) && setting.Value.ValueKind == JsonValueKind.Null;
						if (!nullOk && setting.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null)
						{
							throw new ArgumentException("TInvest:" + setting.Name + " must be a scalar value, not null or a container.");
						}
					}
				}

				var value = invest.HasValue ? Property(invest.Value, "RecordSymbols") : null;
				fileArray |= value.HasValue && value.Value.ValueKind == JsonValueKind.Array;
				if (value.HasValue && (value.Value.ValueKind != JsonValueKind.Array || value.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)))
				{
					throw new ArgumentException("TInvest:RecordSymbols must be an array of strings.");
				}
			}
		}

		if (records.Value == string.Empty && !fileArray)
		{
			throw new ArgumentException("TInvest:RecordSymbols must be an array of strings.");
		}

		var options = section.Get<TInvestOptions>() ?? new TInvestOptions();
		options.Validate();
		return options;
	}

	static JsonElement? Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).LastOrDefault() : null;
}
