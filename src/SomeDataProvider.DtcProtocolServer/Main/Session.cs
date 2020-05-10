namespace SomeDataProvider.DtcProtocolServer.Main
{
	using System;
	using System.Runtime.InteropServices;

	using Microsoft.Extensions.Logging;

	using NBLib.Logging;

	using NetCoreServer;

	using SomeDataProvider.DtcProtocolServer.DtcProtocol;
	using SomeDataProvider.DtcProtocolServer.DtcProtocol.Enums;

	public class Session : TcpSession
	{
		public Session(TcpServer server, ILoggerFactory loggerFactory)
			: base(server)
		{
			L = loggerFactory.CreateLogger<Session>();
		}

		ILogger<Session> L { get; }

		protected override void OnReceived(byte[] buffer, long offset, long size)
		{
			try
			{
				L.LogOperation(() =>
				{
					var messageType = (MessageTypeEnum)BitConverter.ToUInt16(buffer.AsSpan(2, 2));
					switch (messageType)
					{
						case MessageTypeEnum.EncodingRequest:
							{
								L.LogInformation("RequestReceived: {requestType}", MessageTypeEnum.EncodingRequest);
								var encodingRequest = ByteArrayToStruct<EncodingRequest>(buffer);
								Send(StructToByteArray(new EncodingResponse
								{
									Size = encodingRequest.Size,
									Type = MessageTypeEnum.EncodingResponse,
									ProtocolVersion = encodingRequest.ProtocolVersion,
									Encoding = EncodingEnum.JsonEncoding,
								}));
								break;
							}
						default:
							throw new NotSupportedException();
					}
				}, "ProcessRequest");
			}
			catch (Exception ex) when (!(ex is OperationCanceledException))
			{
				L.LogError(ex, "Error while processing request.");
			}
		}

		static T ByteArrayToStruct<T>(byte[] bytes)
			where T : struct
		{
			GCHandle handle = default;
			T result;
			try
			{
				handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
				result = (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T))!;
			}
			finally
			{
				if (handle.IsAllocated)
				{
					handle.Free();
				}
			}
			return result;
		}

		static byte[] StructToByteArray<T>(T obj)
			where T : struct
		{
			var size = Marshal.SizeOf(obj);
			var result = new byte[size];
			GCHandle handle = default;
			try
			{
				handle = GCHandle.Alloc(result, GCHandleType.Pinned);
				Marshal.StructureToPtr(obj, handle.AddrOfPinnedObject(), false);
			}
			finally
			{
				if (handle.IsAllocated)
				{
					handle.Free();
				}
			}
			return result;
		}
	}
}