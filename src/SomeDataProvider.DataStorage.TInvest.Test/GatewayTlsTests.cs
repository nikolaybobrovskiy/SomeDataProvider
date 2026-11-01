namespace SomeDataProvider.DataStorage.TInvest.Test;

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NUnit.Framework;

[TestFixture]
public class GatewayTlsTests
{
	[Test]
	[Explicit("Authorized local TLS preflight only; no token and no API method.")]
	[Category("TInvestSandboxTls")]
	public async Task SandboxTlsHandshakeWithoutCredentials()
	{
		if (Environment.GetEnvironmentVariable("TINVEST_SANDBOX_TLS_CHECK") != "1")
		{
			Assert.Ignore("An explicit TLS preflight authorization is required.");
		}

		var options = new TInvestOptions { Environment = "Sandbox" };
		Assert.That(options.IsSandbox, Is.True);
		using var handler = SdkTInvestGateway.CreateHandler();
		handler.AllowAutoRedirect = false;
		using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
		using var request = new HttpRequestMessage(HttpMethod.Get, SdkTInvestGateway.Endpoint(options) + "/")
		{
			Version = HttpVersion.Version20,
			VersionPolicy = HttpVersionPolicy.RequestVersionExact,
		};
		var clock = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
			TestContext.Out.WriteLine("sandbox_tls=true token=false api_method=false http_status=" + (int)response.StatusCode + " tls_http_ms=" + clock.ElapsedMilliseconds);
		}
		catch (Exception ex)
		{
			Assert.Fail("Sandbox TLS preflight failed; exception=" + ex.GetType().Name + "; elapsed_ms=" + clock.ElapsedMilliseconds);
		}
	}

	[Test]
	public void HandlerUsesOnlyBundledRootAndNeverDownloadsCertificates()
	{
		using var handler = SdkTInvestGateway.CreateHandler();
		var policy = handler.SslOptions.CertificateChainPolicy!;
		Assert.That(policy.TrustMode, Is.EqualTo(X509ChainTrustMode.CustomRootTrust));
		Assert.That(policy.DisableCertificateDownloads, Is.True);
		Assert.That(policy.RevocationMode, Is.EqualTo(X509RevocationMode.NoCheck));
		Assert.That(policy.VerificationFlags, Is.EqualTo(X509VerificationFlags.NoFlag));
		Assert.That(policy.CustomTrustStore.Count, Is.EqualTo(1));
		Assert.That(policy.ExtraStore.Count, Is.Zero);
		Assert.That(policy.ApplicationPolicy.Cast<Oid>().Select(o => o.Value), Is.EqualTo(new[] { "1.3.6.1.5.5.7.3.1" }));
		using var resource = typeof(Tinkoff.InvestApi.InvestApiSettings).Assembly.GetManifestResourceStream("Tinkoff.InvestApi.Resources.Certificates.Russian_Trusted_Root_CA.cer")!;
		using var bytes = new MemoryStream();
		resource.CopyTo(bytes);
		using var expected = X509CertificateLoader.LoadCertificate(bytes.ToArray());
		Assert.That(policy.CustomTrustStore[0].RawData, Is.EqualTo(expected.RawData));
	}

	[TestCase(true, "localhost", true, true)]
	[TestCase(false, "localhost", true, false)]
	[TestCase(true, "wrong.invalid", true, false)]
	[TestCase(true, "localhost", false, false)]
	public async Task HandlerValidatesPinnedChainHostAndServerUsage(bool pinnedIssuer, string host, bool serverUsage, bool accepted)
	{
		using var root = CreateRoot("invented-pinned-root");
		using var other = CreateRoot("invented-other-root");
		using var leaf = CreateLeaf(pinnedIssuer ? root : other, host, serverUsage);
		using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var server = ServeAsync(listener, leaf, stop.Token);
		using var handler = SdkTInvestGateway.CreateHandler(root);
		handler.UseProxy = false;
		SslPolicyErrors? observedErrors = null;
		var validation = handler.SslOptions.RemoteCertificateValidationCallback!;
		handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
		{
			observedErrors = errors;
			return validation(sender, certificate, chain, errors);
		};
		using var client = new HttpClient(handler);
		var passed = false;
		try
		{
			using var response = await client.GetAsync("https://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/", stop.Token);
			passed = response.StatusCode == HttpStatusCode.OK;
		}
		catch (HttpRequestException ex)
		{
			// Only invented loopback traffic is used in this test.
			TestContext.Out.WriteLine("loopback_client_error=" + ex.GetType().Name);
		}
		finally
		{
			stop.Cancel();
			await server;
		}

		Assert.That(observedErrors.HasValue, Is.True, "The real handler reached certificate validation.");
		Assert.That(observedErrors, Is.EqualTo(!pinnedIssuer || !serverUsage ? SslPolicyErrors.RemoteCertificateChainErrors : host != "localhost" ? SslPolicyErrors.RemoteCertificateNameMismatch : SslPolicyErrors.None));
		Assert.That(passed, Is.EqualTo(accepted));
	}

	[TestCase(SslPolicyErrors.None)]
	[TestCase(SslPolicyErrors.RemoteCertificateNotAvailable)]
	public void MissingCertificateNeverPassesEvenWithoutAnErrorFlag(SslPolicyErrors errors) =>
		Assert.That(SdkTInvestGateway.ValidateCertificate(this, null, null, errors), Is.False);

	static X509Certificate2 CreateRoot(string name)
	{
		using var key = RSA.Create(2048);
		var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
		request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
		return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
	}

	static X509Certificate2 CreateLeaf(X509Certificate2 root, string host, bool serverUsage)
	{
		using var key = RSA.Create(2048);
		var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
		request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
		request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(serverUsage ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
		var san = new SubjectAlternativeNameBuilder();
		san.AddDnsName(host);
		request.CertificateExtensions.Add(san.Build());
		using var certificate = request.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
		using var withKey = certificate.CopyWithPrivateKey(key);
		// Schannel cannot use CopyWithPrivateKey's ephemeral RSA key for a TLS server.
		return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
	}

	static async Task ServeAsync(TcpListener listener, X509Certificate2 certificate, CancellationToken stop)
	{
		try
		{
			using var socket = await listener.AcceptTcpClientAsync(stop);
			using var tls = new SslStream(socket.GetStream());
			await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, stop);
			var request = new byte[4096];
			if (await tls.ReadAsync(request, stop) != 0)
			{
				await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop);
			}
		}
		catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException)
		{
			TestContext.Out.WriteLine("loopback_server_error=" + ex.GetType().Name);
		}
	}
}
