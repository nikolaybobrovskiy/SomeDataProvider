namespace SomeDataProvider.DataStorage.TInvest;

using System.Collections.Frozen;
using System.Text;

/// <summary>Owned bulk rows and their board hierarchy. No definition hydration or mutable source collections.</summary>
public sealed class TInvestCatalogSnapshot
{
	static readonly (TInvestInstrumentKind Kind, string ID, string Name, int WireKind)[] Kinds =
	{
		(TInvestInstrumentKind.Share, "shares", "Shares", 1), (TInvestInstrumentKind.Bond, "bonds", "Bonds", 2),
		(TInvestInstrumentKind.Etf, "etfs", "ETFs", 3), (TInvestInstrumentKind.Future, "futures", "Futures", 4),
		(TInvestInstrumentKind.Currency, "currencies", "Currencies", 5),
	};
	static readonly UTF8Encoding StrictUtf8 = new(false, true);
	readonly FrozenDictionary<string, IReadOnlyList<TInvestCatalogRow>> _nodes;

	internal TInvestCatalogSnapshot(IEnumerable<TInvestInstrument> source, DateTimeOffset created)
	{
		Version = Guid.NewGuid().ToString();
		Created = created;
		var identities = new Dictionary<string, TInvestInstrument>(StringComparer.Ordinal);
		foreach (var instrument in source)
		{
			var s = instrument.Short;
			var symbol = s.Ticker + "-" + s.ClassCode;
			if (s.Ticker.Length == 0 || s.ClassCode.Length == 0 || s.ClassCode.Contains('-') || symbol.StartsWith("fred-", StringComparison.OrdinalIgnoreCase) ||
				!symbol.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') || !Text(symbol, 64) || !Text(s.Name, 1024) ||
				!Text(s.Uid, 256) || string.IsNullOrWhiteSpace(instrument.Board) || !Text(instrument.Board, 256) ||
				!Text(instrument.Currency, 64, allowEmpty: true)) throw new TInvestCatalogException("InvalidSourceData");
			if (identities.TryGetValue(symbol, out var previous) && previous != instrument) throw new TInvestCatalogException("ConflictingIdentity");
			identities[symbol] = instrument;
		}
		Instruments = Array.AsReadOnly(identities.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray());
		var nodes = new Dictionary<string, IReadOnlyList<TInvestCatalogRow>>(StringComparer.Ordinal)
		{
			[string.Empty] = Array.AsReadOnly(new[] { new TInvestCatalogRow("t-bank", "T-Bank", null) }),
			["t-bank"] = Array.AsReadOnly(Kinds.Select(k => new TInvestCatalogRow("t-bank/" + k.ID, k.Name, "t-bank")).ToArray()),
		};
		foreach (var kind in Kinds)
		{
			var parent = "t-bank/" + kind.ID;
			var groups = Instruments.Where(i => i.Short.Kind == kind.Kind).GroupBy(i => i.Board!).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
			var boards = new List<TInvestCatalogRow>();
			foreach (var group in groups)
			{
				var node = parent + "/" + Uri.EscapeDataString(group.Key);
				if (!Text(node, 256)) throw new TInvestCatalogException("InvalidSourceData");
				boards.Add(new(node, group.Key, parent));
				nodes[node] = Array.AsReadOnly(group.Select(i => new TInvestCatalogRow(node, i.Short.Name, parent, i, kind.WireKind)).ToArray());
			}
			nodes[parent] = boards.AsReadOnly();
		}
		_nodes = nodes.ToFrozenDictionary(StringComparer.Ordinal);
	}

	public string Version { get; }
	public DateTimeOffset Created { get; }
	public IReadOnlyList<TInvestInstrument> Instruments { get; }

	public IReadOnlyList<TInvestCatalogRow> GetRows(string node, string filter)
	{
		if (!_nodes.TryGetValue(node, out var rows)) throw new TInvestCatalogException("UnknownNode");
		if (filter.Length == 0) return rows;
		return Array.AsReadOnly(rows.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
			(r.Instrument is { } i && (i.Short.Ticker + "-" + i.Short.ClassCode).Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray());
	}

	static bool Text(string value, int maxBytes, bool allowEmpty = false)
	{
		if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Any(char.IsControl)) return false;
		try { return StrictUtf8.GetByteCount(value) <= maxBytes; }
		catch (EncoderFallbackException) { return false; }
	}
}
