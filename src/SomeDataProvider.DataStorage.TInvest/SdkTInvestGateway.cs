namespace SomeDataProvider.DataStorage.TInvest;

using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Tinkoff.InvestApi.V1;

/// <summary>The only bridge implementation that depends on SDK types.</summary>
public sealed partial class SdkTInvestGateway : ITInvestGateway
{
	// Keep the single public trust anchor alive for all channels; never change the OS root stores.
	static readonly Lazy<X509Certificate2> BundledRoot = new(LoadBundledRoot);

	readonly GrpcChannel? _channel;
	readonly InstrumentsService.InstrumentsServiceClient _instruments;
	readonly UsersService.UsersServiceClient _users;
	readonly MarketDataService.MarketDataServiceClient _marketData;
	readonly TInvestRateLimiter _limiter;
	readonly bool _sandbox;
	readonly TimeProvider _clock;
	readonly ILogger<SdkTInvestGateway>? _logger;
	readonly TInvestToken? _token;
	readonly bool _includeDealer;
	readonly CallInvoker _marketInvoker;
	readonly int _maximumCandleCalls;

	public SdkTInvestGateway(TInvestToken token, TInvestOptions options, TInvestRateLimiter limiter, TimeProvider? clock = null, ILogger<SdkTInvestGateway>? logger = null)
		: this(token, options, limiter, clock, null, logger)
	{
	}

	internal SdkTInvestGateway(TInvestToken token, TInvestOptions options, TInvestRateLimiter limiter, TimeProvider? clock, HttpMessageHandler? handler, ILogger<SdkTInvestGateway>? logger = null)
	{
		options.Validate();
		_logger = logger;
		_maximumCandleCalls = options.MaxCandleCallsPerRequest;
		_includeDealer = options.IncludeDealerTrades;
		_token = token;
		_limiter = limiter;
		_clock = clock ?? TimeProvider.System;
		_sandbox = options.IsSandbox;
		var credentials = CallCredentials.FromInterceptor((_, metadata) =>
		{
			metadata.Add("authorization", "Bearer " + token.Value);
			metadata.Add("x-app-name", "SomeDataProvider");
			return Task.CompletedTask;
		});
		_channel = GrpcChannel.ForAddress(Endpoint(options), new GrpcChannelOptions
		{
			Credentials = ChannelCredentials.Create(new SslCredentials(), credentials),
			HttpHandler = handler ?? CreateHandler(),
			DisposeHttpClient = true,
			ThrowOperationCanceledOnCancellation = true,
			// No service-config retries or SDK RetriableCallInvoker (amendment A-7).
			MaxRetryAttempts = 1,
		});
		var invoker = _channel.CreateCallInvoker();
		_instruments = new InstrumentsService.InstrumentsServiceClient(invoker);
		_users = new UsersService.UsersServiceClient(invoker);
		_marketData = new MarketDataService.MarketDataServiceClient(invoker);
		_marketInvoker = invoker;
	}

	internal SdkTInvestGateway(CallInvoker invoker, TInvestOptions options, TInvestRateLimiter limiter, TimeProvider clock)
	{
		options.Validate();
		_maximumCandleCalls = options.MaxCandleCallsPerRequest;
		_includeDealer = options.IncludeDealerTrades;
		_limiter = limiter;
		_clock = clock;
		_sandbox = options.IsSandbox;
		_instruments = new InstrumentsService.InstrumentsServiceClient(invoker);
		_users = new UsersService.UsersServiceClient(invoker);
		_marketData = new MarketDataService.MarketDataServiceClient(invoker);
		_marketInvoker = invoker;
	}

	public string? LastFailureStatus { get; private set; }
	public string? LastFailureCode { get; private set; }
	internal GrpcChannel? Channel => _channel;

	public async Task<TInvestAccess> CheckAccessAsync(DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		if (_sandbox)
		{
			await CallAsync(TInvestLimitGroup.Users, () => _users.GetInfoAsync(new GetInfoRequest(), deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false);
			return new(true, Array.Empty<bool>());
		}
		var response = await CallAsync(TInvestLimitGroup.Users, () => _users.GetAccountsAsync(new GetAccountsRequest(), deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false);
		return new(true, response.Accounts.Select(a => a.AccessLevel == AccessLevel.AccountAccessLevelReadOnly).ToArray());
	}

	public async Task<TInvestInstrument?> GetInstrumentAsync(string ticker, string classCode, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		InstrumentResponse response;
		try
		{
			response = await CallAsync(TInvestLimitGroup.Instruments, () => _instruments.GetInstrumentByAsync(
				new InstrumentRequest { IdType = InstrumentIdType.Ticker, Id = ticker, ClassCode = classCode }, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken, allowNotFound: true).ConfigureAwait(false);
		}
		catch (InstrumentMissingException) { return null; }
		var i = response.Instrument;
		var shortInstrument = new TInvestInstrumentShort(i.Ticker, i.ClassCode, i.Uid, i.Name, Kind(i.InstrumentKind), i.Lot, i.ApiTradeAvailableFlag);
		var instrument = new TInvestInstrument(shortInstrument, Decimal(i.MinPriceIncrement), i.Currency, Figi: i.Figi,
			FirstMinuteCandle: i.First1MinCandleDate?.ToDateTimeOffset(), FirstDailyCandle: i.First1DayCandleDate?.ToDateTimeOffset());
		var request = new InstrumentRequest { IdType = InstrumentIdType.Uid, Id = i.Uid };
		if (i.InstrumentKind == InstrumentType.Futures)
		{
			var detail = (await CallAsync(TInvestLimitGroup.Instruments, () => _instruments.FutureByAsync(request, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false)).Instrument;
			instrument = instrument with { IncrementAmount = Decimal(detail.MinPriceIncrementAmount), BasicAssetSize = Decimal(detail.BasicAssetSize), Expiration = detail.ExpirationDate?.ToDateTimeOffset() };
		}
		else if (i.InstrumentKind == InstrumentType.Bond)
		{
			var detail = (await CallAsync(TInvestLimitGroup.Instruments, () => _instruments.BondByAsync(request, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false)).Instrument;
			instrument = instrument with { Nominal = Money(detail.Nominal) };
		}
		else if (i.InstrumentKind == InstrumentType.Option)
		{
			var detail = (await CallAsync(TInvestLimitGroup.Instruments, () => _instruments.OptionByAsync(request, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false)).Instrument;
			// AssetType names an asset class, not whether the option is on a future.
			// Resolve the contractual underlying position instead of guessing string values.
			Instrument? underlying = null;
			if (detail.BasicAssetPositionUid.Length != 0)
			{
				try
				{
					underlying = (await CallAsync(TInvestLimitGroup.Instruments,
						() => _instruments.GetInstrumentByAsync(new InstrumentRequest { IdType = InstrumentIdType.PositionUid, Id = detail.BasicAssetPositionUid },
							deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken, allowNotFound: true).ConfigureAwait(false)).Instrument;
				}
				catch (InstrumentMissingException) { /* No definable underlying: cache this option as unsupported. */ }
			}
			var kind = underlying?.InstrumentKind switch
			{
				InstrumentType.Share => TInvestInstrumentKind.StockOption,
				InstrumentType.Futures => TInvestInstrumentKind.FuturesOption,
				InstrumentType.Index => TInvestInstrumentKind.IndexOption,
				_ => TInvestInstrumentKind.Unsupported,
			};
			instrument = instrument with { Short = shortInstrument with { Kind = kind }, Expiration = detail.ExpirationDate?.ToDateTimeOffset() };
		}
		return instrument;
	}

	public async Task<IReadOnlyList<TInvestInstrumentShort>> FindInstrumentsAsync(string query, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		var response = await CallAsync(TInvestLimitGroup.Instruments, () => _instruments.FindInstrumentAsync(new FindInstrumentRequest { Query = query }, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken).ConfigureAwait(false);
		return response.Instruments.Select(i => new TInvestInstrumentShort(i.Ticker, i.ClassCode, i.Uid, i.Name, Kind(i.InstrumentKind), i.Lot, i.ApiTradeAvailableFlag)).ToArray();
	}

	public Task<TInvestRateLimiter.Reservation> ReserveCandlesAsync(int calls, DateTimeOffset deadline, CancellationToken cancellationToken) =>
		_limiter.ReserveAsync(TInvestLimitGroup.MarketData, calls, deadline, cancellationToken);

	public async Task<IReadOnlyList<TInvestCandle>> GetCandlesAsync(TInvestCandleRequest request, TInvestRateLimiter.Reservation reservation, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		var interval = request.IntervalSeconds switch
		{
			5 => CandleInterval._5Sec, 10 => CandleInterval._10Sec, 30 => CandleInterval._30Sec,
			60 => CandleInterval._1Min, 120 => CandleInterval._2Min, 180 => CandleInterval._3Min,
			300 => CandleInterval._5Min, 600 => CandleInterval._10Min, 900 => CandleInterval._15Min,
			1800 => CandleInterval._30Min, 3600 => CandleInterval.Hour, 7200 => CandleInterval._2Hour,
			14400 => CandleInterval._4Hour, 86400 => CandleInterval.Day, 604800 => CandleInterval.Week,
			2592000 => CandleInterval.Month,
			_ => throw new TInvestRequestException("Interval not supported by T-Invest.", TInvestFailureKind.Unsupported),
		};
		var response = await CallAsync(TInvestLimitGroup.MarketData, () => _marketData.GetCandlesAsync(new GetCandlesRequest
		{
			InstrumentId = request.Uid,
			From = Timestamp.FromDateTimeOffset(request.Start),
			To = Timestamp.FromDateTimeOffset(request.End),
			Interval = interval,
			// API error 30220 forbids limit with an explicit source. Time pages enforce the cap instead.
			CandleSourceType = request.IncludeDealer ? GetCandlesRequest.Types.CandleSource.IncludeWeekend : GetCandlesRequest.Types.CandleSource.Exchange,
		}, deadline: deadline.UtcDateTime, cancellationToken: cancellationToken), deadline, cancellationToken, reservation: reservation).ConfigureAwait(false);
		return response.Candles.Select(c => new TInvestCandle(c.Time.ToDateTimeOffset(), (double)Decimal(c.Open), (double)Decimal(c.High),
			(double)Decimal(c.Low), (double)Decimal(c.Close), c.Volume, c.IsComplete, c.CandleSource == CandleSource.Exchange)).ToArray();
	}

	public void Dispose() => _channel?.Dispose();

	internal static string Endpoint(TInvestOptions options) => options.IsSandbox
		? "https://sandbox-invest-public-api.tbank.ru:443" : "https://invest-public-api.tbank.ru:443";

	internal static bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
		certificate != null && errors == SslPolicyErrors.None;

	internal static SocketsHttpHandler CreateHandler() => CreateHandler(BundledRoot.Value);

	internal static SocketsHttpHandler CreateHandler(X509Certificate2 root)
	{
		var policy = new X509ChainPolicy
		{
			TrustMode = X509ChainTrustMode.CustomRootTrust,
			DisableCertificateDownloads = true,
			RevocationMode = X509RevocationMode.NoCheck,
		};
		policy.CustomTrustStore.Add(root);
		policy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
		return new SocketsHttpHandler
		{
			EnableMultipleHttp2Connections = true,
			SslOptions = new SslClientAuthenticationOptions
			{
				CertificateChainPolicy = policy,
				// The native validation above checks both this chain policy and the host name.
				RemoteCertificateValidationCallback = ValidateCertificate,
			},
		};
	}

	static X509Certificate2 LoadBundledRoot()
	{
		using var stream = typeof(Tinkoff.InvestApi.InvestApiSettings).Assembly.GetManifestResourceStream("Tinkoff.InvestApi.Resources.Certificates.Russian_Trusted_Root_CA.cer")
			?? throw new InvalidOperationException("The pinned T-Invest trust anchor is missing from the SDK.");
		using var bytes = new MemoryStream();
		stream.CopyTo(bytes);
		return X509CertificateLoader.LoadCertificate(bytes.ToArray());
	}

	static TInvestInstrumentKind Kind(InstrumentType kind) => kind switch
	{
		InstrumentType.Share => TInvestInstrumentKind.Share,
		InstrumentType.Etf => TInvestInstrumentKind.Etf,
		InstrumentType.Bond => TInvestInstrumentKind.Bond,
		InstrumentType.Futures => TInvestInstrumentKind.Future,
		InstrumentType.Currency => TInvestInstrumentKind.Currency,
		InstrumentType.Option => TInvestInstrumentKind.StockOption, // Refined by OptionBy before a definition is sent.
		_ => TInvestInstrumentKind.Unsupported,
	};

	static decimal Decimal(Quotation? value) => value == null ? 0 : value.Units + value.Nano / 1_000_000_000m;
	static decimal Money(MoneyValue? value) => value == null ? 0 : value.Units + value.Nano / 1_000_000_000m;

	async Task<T> CallAsync<T>(TInvestLimitGroup group, Func<AsyncUnaryCall<T>> createCall, DateTimeOffset deadline, CancellationToken cancellationToken, bool allowNotFound = false, TInvestRateLimiter.Reservation? reservation = null)
	{
		for (var attempt = 0; ; attempt++)
		{
			if (attempt == 0 && reservation != null) await reservation.AcquireAsync(_limiter, deadline, cancellationToken).ConfigureAwait(false);
			else if (reservation != null) await reservation.AcquireRetryAsync(_limiter, _maximumCandleCalls, deadline, cancellationToken).ConfigureAwait(false);
			else await _limiter.AcquireAsync(group, deadline, cancellationToken).ConfigureAwait(false);
			Metadata? responseHeaders = null;
			try
			{
				using var call = createCall();
				responseHeaders = await call.ResponseHeadersAsync.ConfigureAwait(false);
				Correct(group, responseHeaders, false);
				return await call.ResponseAsync.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				LastFailureStatus = StatusCode.DeadlineExceeded.ToString();
				throw new TInvestRequestException("T-Invest request limit reached; retry in 1 s.", TInvestFailureKind.Limit);
			}
			catch (RpcException ex)
			{
				LastFailureStatus = ex.StatusCode.ToString();
				LastFailureCode = Regex.Match(_token?.Redact(ex.Status.Detail) ?? ex.Status.Detail, @"\b[0-9]{5}\b", RegexOptions.CultureInvariant).Value;
				if (ex.StatusCode is StatusCode.Cancelled or StatusCode.DeadlineExceeded)
				{
					if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
					if (ex.StatusCode == StatusCode.DeadlineExceeded || _clock.GetUtcNow() >= deadline)
						throw new TInvestRequestException("T-Invest request limit reached; retry in 1 s.", TInvestFailureKind.Limit);
				}
				if (allowNotFound && ex.StatusCode is StatusCode.NotFound or StatusCode.InvalidArgument)
				{
					_logger?.LogDebug("T-Invest identifier lookup returned {status}.", LastFailureStatus);
					throw new InstrumentMissingException();
				}
				var number = Regex.Match(_token?.Redact(ex.Status.Detail) ?? ex.Status.Detail, @"\b[0-9]{5}\b", RegexOptions.CultureInvariant).Value;
				var message = Regex.Replace(_token?.Redact(ex.Trailers.GetValue("message") ?? string.Empty) ?? string.Empty, @"t\.[A-Za-z0-9_-]{40,}", "<redacted>", RegexOptions.CultureInvariant);
				_logger?.LogWarning("T-Invest upstream request failed: {status}, code {code}, message {message}", LastFailureStatus, number, message);

				if (ex.StatusCode == StatusCode.ResourceExhausted)
				{
					Correct(group, ex.Trailers.GetValue("x-ratelimit-reset") != null ? ex.Trailers : responseHeaders ?? ex.Trailers, true);
					if (attempt == 0 && _clock.GetUtcNow() < deadline) continue;
					throw new TInvestRequestException("T-Invest request limit reached; retry in 60 s.", TInvestFailureKind.Limit);
				}
				throw new TInvestRequestException(ex.StatusCode == StatusCode.Unauthenticated
					? "T-Invest token is missing, expired or for the other environment."
					: "T-Invest source request failed.");
			}
		}
	}

	void Correct(TInvestLimitGroup group, Metadata headers, bool exhausted)
	{
		int? Read(string name) => int.TryParse(headers.GetValue(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
		var reset = Read("x-ratelimit-reset");
		_limiter.Correct(group, Read("x-ratelimit-limit"), Read("x-ratelimit-remaining"), TimeSpan.FromSeconds(Math.Clamp(reset ?? 60, 1, 3600)), exhausted);
	}

	sealed class InstrumentMissingException : Exception
	{
	}
}
