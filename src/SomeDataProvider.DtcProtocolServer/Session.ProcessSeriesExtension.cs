// This is an independent project of an individual developer. Dear PVS-Studio, please check it.
// PVS-Studio Static Code Analyzer for C, C++ and C#: http://www.viva64.com

namespace SomeDataProvider.DtcProtocolServer;

using System;
using System.Threading;
using System.Threading.Tasks;

using Dtc.Protocol;
using Dtc.Protocol.Private;

using Microsoft.Extensions.Logging;

using NBLib.Logging;

using SomeDataProvider.DtcProtocolServer.Data;
using SomeDataProvider.DtcProtocolServer.DtcProtocol;

// The private series extension v1 (docs/dtc-series-extension.md in MrktPltfrm): the handshake, structured series metadata,
// and observations by real-time period. Both the streaming and the history-only server answer it. The session keeps no
// handshake state: it answers metadata and vintages requests whenever they arrive, and each gets its answer or exactly
// one reject.
partial class Session
{
	/// <summary>The features the handshake announces: metadata and vintages, which FRED both supports.</summary>
	internal const SeriesExtensionFeatures OfferedSeriesExtensionFeatures = SeriesExtensionFeatures.Metadata | SeriesExtensionFeatures.Vintages;

	/// <summary>
	/// The longest Symbol a series request may have, in UTF-8 bytes: far longer than any FRED symbol, and short enough that
	/// every answer that echoes it fits a message.
	/// </summary>
	internal const int MaxSeriesSymbolLength = 256;

	/// <summary>Whether the session uses binary encoding with variable-length strings, the only encoding of the extension.</summary>
	bool IsVlsSession => _currentMessageProtocol.Encoding == EncodingEnum.BINARY_WITH_VARIABLE_LENGTH_STRINGS;

	/// <summary>What makes a series request's Symbol and Exchange invalid under Provider Profile v1; null when they are valid.</summary>
	static string? CheckSeriesSymbol(string symbol, string exchange)
	{
		if (exchange.Length != 0)
			return "Exchange must be empty: Provider Profile v1 symbols have no exchange.";
		if (symbol.Length == 0)
			return "Symbol must not be empty.";
		if (MessageProtocol.SeriesTextEncoding.GetByteCount(symbol) > MaxSeriesSymbolLength)
			return $"Symbol must not be longer than {MaxSeriesSymbolLength} bytes.";
		return null;
	}

	/// <summary>What makes a vintages request's windows invalid; null when they are valid.</summary>
	static string? CheckSeriesVintagesWindows(SeriesVintagesRequest request)
	{
		if (request.ObservationStart > request.ObservationEnd)
			return "ObservationStart must not be after ObservationEnd.";
		if (request.RealtimeStart > request.RealtimeEnd)
			return "RealtimeStart must not be after RealtimeEnd.";
		return null;
	}

	void ProcessSeriesExtensionRequest(IMessageDecoder decoder, IMessageEncoder encoder)
	{
		var request = decoder.DecodeSeriesExtensionRequest();
		// The smaller of the client's highest version and the server's; 0 when the client offers none.
		var version = Math.Clamp(request.Version, 0, DtcPrivateMessages.SeriesExtensionVersion);
		var response = new SeriesExtensionResponse
		{
			RequestID = request.RequestID,
			Version = version,
			Features = version == 0 ? SeriesExtensionFeatures.None : OfferedSeriesExtensionFeatures,
		};
		L.LogInformation("SeriesExtensionRequest: {requestId}/{version}; Answer: SeriesExtensionResponse: {agreedVersion}/{features}", request.RequestID, request.Version, response.Version, response.Features);
		encoder.EncodeSeriesExtensionResponse(response);
		SendResponse(encoder.GetEncodedMessage());
	}

	async Task ProcessSeriesMetadataRequestAsync(IMessageDecoder decoder, CancellationToken ct)
	{
		SeriesMetadataRequest request;
		try
		{
			request = decoder.DecodeSeriesMetadataRequest();
		}
		catch (InvalidSeriesRequestException ex)
		{
			L.LogException(ex, LogLevel.Warning, "Invalid SeriesMetadataRequest.");
			await RejectSeriesRequestAsync(ex.RequestId, SeriesRequestRejectReason.InvalidRequest, ex.Message);
			return;
		}
		L.LogInformation("SeriesMetadataRequest: {requestId}/{symbol}/{exchange}", request.RequestID, request.Symbol, request.Exchange);
		if (CheckSeriesSymbol(request.Symbol, request.Exchange) is { } problem)
		{
			await RejectSeriesRequestAsync(request.RequestID, SeriesRequestRejectReason.InvalidRequest, problem);
			return;
		}
		SeriesMetadataResponse response;
		try
		{
			var metadata = await _seriesSource.GetSeriesMetadataAsync(request.Symbol, ct);
			response = metadata with { RequestID = request.RequestID, Symbol = request.Symbol, Exchange = request.Exchange };
		}
		catch (SeriesSourceException ex)
		{
			await RejectSeriesRequestAsync(request.RequestID, ex.Reason, ex.Message, ex.RetryAfter);
			return;
		}
		catch (Exception ex) when (!SeriesVintagesStreamer.IsCancellation(ex, ct))
		{
			L.LogException(ex, LogLevel.Warning, "Reading the series metadata failed.");
			await RejectSeriesRequestAsync(request.RequestID, SeriesRequestRejectReason.SourceUnavailable, "The provider failed while reading the metadata.");
			return;
		}
		try
		{
			await SeriesVintagesStreamer.EncodeAndSendAsync(_currentMessageProtocol.MessageEncoderFactory, SendResponse, encoder => encoder.EncodeSeriesMetadataResponse(response));
		}
		catch (ArgumentException ex)
		{
			// Nothing was sent yet, so the request can still get its one reject.
			L.LogException(ex, LogLevel.Warning, "The series metadata cannot be encoded.");
			await RejectSeriesRequestAsync(request.RequestID, SeriesRequestRejectReason.SourceUnavailable, "The provider cannot encode the metadata.");
			return;
		}
		L.LogInformation("Answer: SeriesMetadataResponse: {frequency}/{seasonalAdjustment}/{unitsTransformation}", response.Frequency, response.SeasonalAdjustment, response.UnitsTransformation);
	}

	async Task ProcessSeriesVintagesRequestAsync(IMessageDecoder decoder, CancellationToken ct)
	{
		SeriesVintagesRequest request;
		try
		{
			request = decoder.DecodeSeriesVintagesRequest();
		}
		catch (InvalidSeriesRequestException ex)
		{
			L.LogException(ex, LogLevel.Warning, "Invalid SeriesVintagesRequest.");
			await RejectSeriesRequestAsync(ex.RequestId, SeriesRequestRejectReason.InvalidRequest, ex.Message);
			return;
		}
		L.LogInformation(
			"SeriesVintagesRequest: {requestId}/{symbol}/{exchange}, observations {observationStart}..{observationEnd}, real time {realtimeStart}..{realtimeEnd}",
			request.RequestID,
			request.Symbol,
			request.Exchange,
			request.ObservationStart,
			request.ObservationEnd,
			request.RealtimeStart,
			request.RealtimeEnd);
		if ((CheckSeriesSymbol(request.Symbol, request.Exchange) ?? CheckSeriesVintagesWindows(request)) is { } problem)
		{
			await RejectSeriesRequestAsync(request.RequestID, SeriesRequestRejectReason.InvalidRequest, problem);
			return;
		}
		await SeriesVintagesStreamer.StreamAsync(
			_seriesSource.GetSeriesVintagesAsync(request, ct),
			request.RequestID,
			_currentMessageProtocol.MessageEncoderFactory,
			SendResponse,
			L,
			ct);
	}

	async Task RejectSeriesRequestAsync(int requestId, SeriesRequestRejectReason reason, string rejectText, TimeSpan? retryAfter = null)
	{
		L.LogInformation("Answer: SeriesRequestReject: {requestId}/{reason}: {rejectText}", requestId, reason, rejectText);
		await SeriesVintagesStreamer.SendRejectAsync(_currentMessageProtocol.MessageEncoderFactory, SendResponse, requestId, reason, rejectText, retryAfter);
	}
}
