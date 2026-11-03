namespace SomeDataProvider.DataStorage.TInvest;

using System.Globalization;
using Microsoft.Extensions.Logging;

/// <summary>
/// Downloads one instrument-day trade archive over plain HTTPS GET, with no token (measured: the host serves the files
/// anonymously). The file is kept as downloaded, after a check of its magic bytes.
/// </summary>
public sealed class TradeArchiveClient : IDisposable
{
	const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;

	readonly HttpClient _http;
	readonly TInvestRateLimiter _limiter;
	readonly TimeProvider _clock;
	readonly ILogger? _logger;

	public TradeArchiveClient(HttpMessageHandler handler, Uri baseAddress, TInvestRateLimiter limiter, TimeProvider clock, ILogger? logger = null)
	{
		_http = new HttpClient(handler, true) { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };
		_limiter = limiter;
		_clock = clock;
		_logger = logger;
	}

	/// <summary>Returns false when the host has no archive for that instrument and day (HTTP 404).</summary>
	public async Task<bool> DownloadAsync(string instrumentId, DateOnly day, string destination, TInvestRateLimiter.Reservation? reservation, DateTimeOffset deadline, CancellationToken cancellationToken)
	{
		var url = $"history-trades/{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}?instrumentId={Uri.EscapeDataString(instrumentId)}";
		for (var attempt = 0; ; attempt++)
		{
			if (attempt == 0 && reservation != null) await reservation.AcquireAsync(_limiter, deadline, cancellationToken).ConfigureAwait(false);
			else await _limiter.AcquireAsync(TInvestLimitGroup.Archives, deadline, cancellationToken).ConfigureAwait(false);
			using var timer = new CancellationTokenSource(Max(deadline - _clock.GetUtcNow()), _clock);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken);
			try
			{
				using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
				var status = (int)response.StatusCode;
				if (status == 404) return false;
				if (status == 429)
				{
					var reset = response.Headers.TryGetValues("x-ratelimit-reset", out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? Math.Clamp(seconds, 1, 3600) : 60;
					_limiter.Correct(TInvestLimitGroup.Archives, null, 0, TimeSpan.FromSeconds(reset), true);
					_logger?.LogWarning("T-Invest archive host answered HTTP 429; blocking archive downloads for {seconds} s.", reset);
					// Retry once, and only if the caller's deadline allows waiting for the reset.
					if (attempt == 0 && _clock.GetUtcNow().AddSeconds(reset) < deadline) continue;
					throw new TInvestRequestException($"T-Invest archive request limit reached; retry in {reset} s.", TInvestFailureKind.Limit);
				}

				if (status is < 200 or > 299)
				{
					_logger?.LogWarning("T-Invest archive host answered HTTP {status}.", status);
					throw new TInvestRequestException("T-Invest archive request failed.");
				}

				await SaveAsync(response, destination, linked.Token).ConfigureAwait(false);
				return true;
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				throw new TInvestRequestException("T-Invest archive request limit reached; retry in 1 s.", TInvestFailureKind.Limit);
			}
			catch (HttpRequestException ex)
			{
				_logger?.LogWarning("T-Invest archive request failed: {type}, status {status}.", ex.GetType().Name, ex.StatusCode);
				throw new TInvestRequestException("T-Invest archive request failed.");
			}
		}
	}

	public void Dispose() => _http.Dispose();

	static async Task SaveAsync(HttpResponseMessage response, string destination, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		var temporary = destination + ".part-" + Guid.NewGuid().ToString("N");
		try
		{
			await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
			await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
			{
				var buffer = new byte[65536];
				var total = 0L;
				var head = new byte[4];
				var headLength = 0;
				var checkedFormat = false;
				int read;
				while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
				{
					// A read may return fewer bytes than the magic needs, so collect up to four bytes first.
					if (!checkedFormat)
					{
						var take = Math.Min(read, head.Length - headLength);
						buffer.AsSpan(0, take).CopyTo(head.AsSpan(headLength));
						headLength += take;
						if (headLength == head.Length)
						{
							if (!TradeArchiveReader.IsKnownFormat(head)) throw new TInvestRequestException("T-Invest trade archive has an unexpected format.");
							checkedFormat = true;
						}
					}

					total += read;
					if (total > MaximumFileBytes) throw new TInvestRequestException("T-Invest trade archive is larger than the supported maximum.");
					await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
				}

				if (!checkedFormat && !TradeArchiveReader.IsKnownFormat(head.AsSpan(0, headLength))) throw new TInvestRequestException("T-Invest trade archive has an unexpected format.");
			}

			File.Move(temporary, destination, true);
		}
		catch
		{
			try { File.Delete(temporary); }
			catch (IOException) { }
			throw;
		}
	}

	static TimeSpan Max(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;
}
