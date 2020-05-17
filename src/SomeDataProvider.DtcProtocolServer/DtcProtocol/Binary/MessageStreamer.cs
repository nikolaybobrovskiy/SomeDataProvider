// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;

	class MessageStreamer : IMessageStreamer
	{
		int? _longMessageSize;
		int? _longMessageRemainingBytes;
		List<byte[]>? _longMessageChunks;

		public event IMessageStreamer.MessageBytesReceivedHandler? MessageBytesReceived;

		public void PutReceivedBytes(Memory<byte> bytes)
		{
			if (_longMessageRemainingBytes == null)
			{
				var messageSize = BitConverter.ToUInt16(bytes.Span.Slice(0, 2));
				if (bytes.Length < messageSize)
				{
					var firstChunk = bytes.ToArray();
					_longMessageChunks = new List<byte[]> { firstChunk };
					_longMessageRemainingBytes = messageSize - firstChunk.Length;
					_longMessageSize = messageSize;
				}
				else
				{
					MessageBytesReceived?.Invoke(bytes.Slice(0, messageSize));
					if (bytes.Length > messageSize)
					{
						PutReceivedBytes(bytes.Slice(messageSize));
					}
				}
			}
			else
			{
				Debug.Assert(_longMessageChunks != null, "_longMessageChunks should not be null.");
				Debug.Assert(_longMessageSize != null, "_longMessageSize should not be null.");
				var remainingBytes = _longMessageRemainingBytes.Value;
				if (bytes.Length < remainingBytes)
				{
					var nextChunk = bytes.ToArray();
					_longMessageChunks.Add(nextChunk);
					_longMessageRemainingBytes -= nextChunk.Length;
				}
				else
				{
					var lastChunk = bytes.Slice(0, remainingBytes).ToArray();
					_longMessageChunks.Add(lastChunk);
					var messageBuffer = new byte[_longMessageSize.Value];
					var messageBufferIndex = 0;
					foreach (var chunk in _longMessageChunks)
					{
						Array.Copy(chunk, 0, messageBuffer, messageBufferIndex, chunk.Length);
						messageBufferIndex += chunk.Length;
					}
					_longMessageSize = null;
					_longMessageChunks = null;
					_longMessageRemainingBytes = null;
					MessageBytesReceived?.Invoke(messageBuffer.AsMemory());
					if (bytes.Length > remainingBytes)
					{
						PutReceivedBytes(bytes.Slice(remainingBytes));
					}
				}
			}
		}
	}
}