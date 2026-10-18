// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer.DtcProtocol.Binary
{
	using System;
	using System.Buffers.Binary;
	using System.Diagnostics;
	using System.IO;

	using Dtc.Protocol;

	/// <summary>Splits received bytes into DTC Size/Type frames, and keeps an incomplete frame across reads.</summary>
	/// <remarks>
	/// <para>
	/// Frames are passed on in order, each with its Size field equal to its length. A frame that lies within one read
	/// is a slice of that read's buffer; a frame that spans reads is reassembled into its own array.
	/// </para>
	/// <para>
	/// <see cref="DtcFrameReader"/> does the framing, so a Size below the four-byte Size/Type header is invalid:
	/// <see cref="PutReceivedBytes"/> throws <see cref="InvalidDataException"/> after passing on the frames before it.
	/// The frame boundaries are unknown from then on, so later input is discarded; the caller should close the connection.
	/// </para>
	/// </remarks>
	sealed class MessageStreamer : IMessageStreamer
	{
		const int SizeFieldLength = sizeof(ushort);

		// The bytes received so far of a frame that a read ended inside. Until both Size bytes have arrived, the buffer
		// holds just the Size field; then a buffer of the frame's full Size replaces it.
		byte[]? _partialFrame;
		int _partialFrameLength;
		bool _isFaulted;

		public event IMessageStreamer.MessageBytesReceivedHandler? MessageBytesReceived;

		/// <exception cref="InvalidDataException">A frame's Size is below the four-byte Size/Type header.</exception>
		public void PutReceivedBytes(Memory<byte> bytes)
		{
			if (_isFaulted)
				return;
			if (_partialFrame != null)
			{
				var consumed = ContinuePartialFrame(_partialFrame, bytes.Span);
				bytes = bytes.Slice(consumed);
			}
			PassOnCompleteFrames(bytes);
		}

		static ushort ReadSize(ReadOnlySpan<byte> frameStart) => BinaryPrimitives.ReadUInt16LittleEndian(frameStart);

		/// <summary>Passes on every complete frame of the input, and keeps an incomplete tail as the partial frame.</summary>
		void PassOnCompleteFrames(Memory<byte> bytes)
		{
			ReadOnlySpan<byte> input = bytes.Span;
			var offset = 0;
			while (TryReadFrame(ref input, out var frameLength))
			{
				MessageBytesReceived?.Invoke(bytes.Slice(offset, frameLength));
				offset += frameLength;
			}
			if (input.IsEmpty)
				return;
			// TryReadFrame has already validated the Size of a tail that holds both Size bytes.
			var partialFrame = new byte[input.Length < SizeFieldLength ? SizeFieldLength : ReadSize(input)];
			input.CopyTo(partialFrame);
			_partialFrame = partialFrame;
			_partialFrameLength = input.Length;
		}

		/// <summary>Adds input to the partial frame, and passes the frame on once it is complete.</summary>
		/// <returns>The count of input bytes that belong to the partial frame.</returns>
		int ContinuePartialFrame(byte[] partialFrame, ReadOnlySpan<byte> input)
		{
			var consumed = Append(partialFrame, input);
			if (_partialFrameLength < partialFrame.Length)
				return consumed;
			if (partialFrame.Length == SizeFieldLength)
			{
				// The buffer holds just the Size field, which is now complete; no valid frame is this short.
				ReadOnlySpan<byte> sizeField = partialFrame;
				var isComplete = TryReadFrame(ref sizeField, out _);
				Debug.Assert(!isComplete, "A frame of just a Size field should have been rejected.");
				var frame = new byte[ReadSize(partialFrame)];
				partialFrame.CopyTo(frame, 0);
				_partialFrame = partialFrame = frame;
				consumed += Append(partialFrame, input.Slice(consumed));
				if (_partialFrameLength < partialFrame.Length)
					return consumed;
			}
			_partialFrame = null;
			_partialFrameLength = 0;
			MessageBytesReceived?.Invoke(partialFrame);
			return consumed;
		}

		/// <summary>Copies as much input as the partial frame's buffer has room for.</summary>
		int Append(byte[] partialFrame, ReadOnlySpan<byte> input)
		{
			var count = Math.Min(partialFrame.Length - _partialFrameLength, input.Length);
			input.Slice(0, count).CopyTo(partialFrame.AsSpan(_partialFrameLength));
			_partialFrameLength += count;
			return count;
		}

		/// <summary>Reads one complete frame with <see cref="DtcFrameReader"/>, and faults the stream on an invalid Size.</summary>
		bool TryReadFrame(ref ReadOnlySpan<byte> input, out int frameLength)
		{
			try
			{
				var isComplete = DtcFrameReader.TryRead(ref input, out _, out var frame);
				frameLength = frame.Length;
				return isComplete;
			}
			catch (InvalidDataException)
			{
				_isFaulted = true;
				_partialFrame = null;
				_partialFrameLength = 0;
				throw;
			}
		}
	}
}
