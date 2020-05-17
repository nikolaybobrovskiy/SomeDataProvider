// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol
{
	using System;

	interface IMessageStreamer
	{
		delegate void MessageBytesReceivedHandler(Memory<byte> bytes);

		event MessageBytesReceivedHandler? MessageBytesReceived;

		void PutReceivedBytes(Memory<byte> bytes);
	}
}