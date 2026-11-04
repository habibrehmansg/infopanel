using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace InfoPanel.Core.Tests.Images;

/// <summary>
/// A TLS server on 127.0.0.1 whose certificate's issuer is missing and names where to fetch it: an issuer address
/// (authority information access) and a revocation list address (CRL distribution point), both on a second listener
/// on 127.0.0.1, the inward address a hostile server would point a client at. A client building the certificate's
/// chain may download from it; <see cref="InwardConnections"/> counts every connection it gets. The handshake fails
/// (the issuer is never trusted); the server sends only its own certificate and builds its chain offline, so nothing
/// but a client reaches the second listener. Nothing here reaches past loopback.
/// </summary>
internal sealed class HostileCertificateServer : IAsyncDisposable
{
    private readonly TcpListener _tls = new(IPAddress.Loopback, 0);
    private readonly TcpListener _inward = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SslStreamCertificateContext _context;
    private readonly Task _acceptingTls;
    private readonly Task _acceptingInward;
    private readonly List<Task> _connections = [];
    private int _handshakes;
    private int _inwardConnections;

    /// <param name="names">The host names the certificate is for.</param>
    public HostileCertificateServer(params string[] names)
    {
        _tls.Start();
        _inward.Start();
        var inward = $"http://127.0.0.1:{InwardPort}";
        _context = SslStreamCertificateContext.Create(Certificate(names, $"{inward}/issuer.cer", $"{inward}/issuer.crl"), null, offline: true);
        _acceptingTls = Task.Run(() => AcceptAsync(_tls, ServeTlsAsync));
        _acceptingInward = Task.Run(() => AcceptAsync(_inward, ServeInwardAsync));
    }

    public int Port => ((IPEndPoint)_tls.LocalEndpoint).Port;

    /// <summary>The port of the inward listener the certificate names.</summary>
    public int InwardPort => ((IPEndPoint)_inward.LocalEndpoint).Port;

    /// <summary>Connections the TLS listener accepted (each a handshake begun).</summary>
    public int Handshakes => Volatile.Read(ref _handshakes);

    /// <summary>Connections the inward listener accepted: a client downloading what the certificate names.</summary>
    public int InwardConnections => Volatile.Read(ref _inwardConnections);

    /// <summary>
    /// A certificate for <paramref name="names"/> signed by an issuer of its own that is never sent nor trusted, naming
    /// <paramref name="issuerUrl"/> as where its issuer is and <paramref name="crlUrl"/> as its revocation list.
    /// </summary>
    private static X509Certificate2 Certificate(string[] names, string issuerUrl, string crlUrl)
    {
        var now = DateTimeOffset.UtcNow;
        using var issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issuerRequest = new CertificateRequest("CN=Hostile test issuer", issuerKey, HashAlgorithmName.SHA256);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        issuerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        issuerRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(issuerRequest.PublicKey, false));
        using var issuer = issuerRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={names[0]}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names) san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [issuerUrl]));
        request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([crlUrl]));
        using var signed = request.Create(issuer, now.AddHours(-1), now.AddDays(7), [1, 2, 3, 4]);
        using var withKey = signed.CopyWithPrivateKey(key);
        // Through PKCS#12, so every platform's TLS can use the key.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    private async Task AcceptAsync(TcpListener listener, Func<TcpClient, Task> serve)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            lock (_connections) _connections.Add(Task.Run(() => serve(client)));
        }
    }

    private async Task ServeTlsAsync(TcpClient client)
    {
        Interlocked.Increment(ref _handshakes);
        using (client)
        await using (var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
        {
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = _context }, _stop.Token);
                // A client that accepted the certificate would get nothing more: the connection closes.
            }
            catch (Exception)
            {
                // The client refused the certificate (as it should) or went away.
            }
        }
    }

    /// <summary>Counts the connection, then answers 404 to whatever it asks.</summary>
    private async Task ServeInwardAsync(TcpClient client)
    {
        Interlocked.Increment(ref _inwardConnections);
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                await stream.ReadAsync(buffer, _stop.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
            }
            catch (Exception)
            {
                // Gone, or stopping.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _tls.Stop();
        _inward.Stop();
        await Task.WhenAll(_acceptingTls, _acceptingInward);
        Task[] open;
        lock (_connections) open = [.. _connections];
        await Task.WhenAll(open);
        _stop.Dispose();
    }
}
