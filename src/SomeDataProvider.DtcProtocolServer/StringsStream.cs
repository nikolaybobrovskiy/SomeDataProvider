// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer
{
	using System;
	using System.IO;
	using System.Text;

	public class StringsStream : Stream
	{
		readonly IStringsReceiver _stringsReceiver;

		public StringsStream(IStringsReceiver stringsReceiver)
		{
			_stringsReceiver = stringsReceiver;
		}

		public interface IStringsReceiver
		{
			void ReceiveString(string str);
		}

		public override bool CanRead => false;

		public override bool CanSeek => false;

		public override bool CanWrite => true;

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			var countOffset = 0;
			if (count == 0) return;
			if (buffer[offset + count - 1] == '\n') countOffset++;
			if (count > 1 && buffer[offset + count - 2] == '\r') countOffset++;
			var str = Encoding.UTF8.GetString(buffer, offset, count - countOffset);
			if (str.IndexOf('\n') >= 0)
			{
				var strings = str.Split('\n');
				foreach (var s in strings)
				{
					_stringsReceiver.ReceiveString(s.TrimEnd('\r'));
				}
			}
			else
			{
				_stringsReceiver.ReceiveString(str);
			}
		}
	}
}