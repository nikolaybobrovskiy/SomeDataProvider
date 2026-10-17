// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using System;
	using System.Globalization;

	using Dtc.Protocol;

	/// <summary>Names DTC message types for logs.</summary>
	static class MessageTypeNames
	{
		const string StructurePrefix = "s_";

		/// <summary>
		/// Returns the name of the message structure that has the wire Type <paramref name="type"/>, without its <c>s_</c>
		/// prefix, such as <c>HistoricalPriceDataRequest</c>; or the number, for a Type without a structure.
		/// </summary>
		public static string Get(ushort type)
		{
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
