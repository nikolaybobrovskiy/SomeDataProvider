// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using System;
	using System.Globalization;

	using Dtc.Protocol;
	using Dtc.Protocol.Private;

	/// <summary>Names DTC message types for logs.</summary>
	static class MessageTypeNames
	{
		const string StructurePrefix = "s_";

		/// <summary>
		/// Returns the name of the message structure that has the wire Type <paramref name="type"/>, without its <c>s_</c>
		/// prefix, such as <c>HistoricalPriceDataRequest</c>; the registry name of a private Type of Dtc.Protocol, such as
		/// <c>SERIES_METADATA_REQUEST</c>; or the number, for any other Type.
		/// </summary>
		public static string Get(ushort type)
		{
			if (DtcPrivateMessages.TryGetInfo(type, out var info))
				return info.Name;
			var visitor = default(NameVisitor);
			return DtcBinaryMessages.TryVisit(type, ref visitor) && visitor.Name != null
				? visitor.Name
				: type.ToString(CultureInfo.InvariantCulture);
		}

		struct NameVisitor : IDtcBinaryMessageVisitor
		{
			public string? Name { get; private set; }

			public void Visit<T>()
				where T : unmanaged, IDtcBinaryMessage
			{
				var name = typeof(T).Name;
				Name = name.StartsWith(StructurePrefix, StringComparison.Ordinal) ? name.Substring(StructurePrefix.Length) : name;
			}
		}
	}
}
