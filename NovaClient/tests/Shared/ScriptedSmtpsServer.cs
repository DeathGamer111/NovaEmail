using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NovaEmail.Testing;

internal enum ScriptedSmtpScenario
{
    Accept,
    RejectAuthentication,
    MalformedEhloResponse,
    StallBeforeGreeting,
    DisconnectAfterData,
}

/// <summary>
/// A single-session, loopback-only implicit-TLS SMTP double. It deliberately
/// implements only the commands needed by the transport fault tests.
/// </summary>
internal sealed class ScriptedSmtpsServer : IAsyncDisposable
{
    private static readonly Encoding WireEncoding = new UTF8Encoding(false, true);
    private readonly TcpListener _listener;
    private readonly ScriptedSmtpScenario _scenario;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _serverTask;
    private readonly List<string> _commands = [];
    private int _connectionCount;
    private int _messageCount;

    private ScriptedSmtpsServer(ScriptedSmtpScenario scenario)
    {
        _scenario = scenario;
        Certificate = CreateCertificate();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 1);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serverTask = RunAsync(_lifetime.Token);
    }

    public int Port { get; }
    public X509Certificate2 Certificate { get; }
    public int ConnectionCount => Volatile.Read(ref _connectionCount);
    public int MessageCount => Volatile.Read(ref _messageCount);
    public string? LastMessage { get; private set; }
    public Exception? LastServerException { get; private set; }
    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_commands) return _commands.ToArray();
        }
    }

    public static ScriptedSmtpsServer Start(ScriptedSmtpScenario scenario) => new(scenario);

    public async Task WaitForCompletionAsync(CancellationToken cancellationToken = default) =>
        await _serverTask.WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _connectionCount);
        await using var network = client.GetStream();
        await using var tls = new SslStream(network, leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = Certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is AuthenticationException or IOException or OperationCanceledException)
        {
            LastServerException = exception;
            return;
        }

        if (_scenario == ScriptedSmtpScenario.StallBeforeGreeting)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(
            tls, WireEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        await using var writer = new StreamWriter(
            tls, WireEncoding, bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n",
        };

        await writer.WriteLineAsync("220 localhost NovaEmail scripted SMTP ready").ConfigureAwait(false);
        var hello = await ReadRequiredLineAsync(reader, cancellationToken).ConfigureAwait(false);
        RecordCommand(hello);
        if (!hello.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase) &&
            !hello.StartsWith("HELO ", StringComparison.OrdinalIgnoreCase))
        {
            await writer.WriteLineAsync("500 expected EHLO").ConfigureAwait(false);
            return;
        }

        if (_scenario == ScriptedSmtpScenario.MalformedEhloResponse)
        {
            await writer.WriteLineAsync("this is not an SMTP status line").ConfigureAwait(false);
            return;
        }

        await writer.WriteLineAsync("250-localhost").ConfigureAwait(false);
        await writer.WriteLineAsync("250-AUTH PLAIN").ConfigureAwait(false);
        await writer.WriteLineAsync("250 SIZE 104857600").ConfigureAwait(false);

        var auth = await ReadRequiredLineAsync(reader, cancellationToken).ConfigureAwait(false);
        RecordCommand(auth);
        if (!auth.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            await writer.WriteLineAsync("504 AUTH PLAIN required").ConfigureAwait(false);
            return;
        }
        if (string.Equals(auth, "AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            await writer.WriteLineAsync("334").ConfigureAwait(false);
            _ = await ReadRequiredLineAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        if (_scenario == ScriptedSmtpScenario.RejectAuthentication)
        {
            await writer.WriteLineAsync("535 5.7.8 authentication rejected by scripted double").ConfigureAwait(false);
            return;
        }
        await writer.WriteLineAsync("235 2.7.0 authenticated").ConfigureAwait(false);

        var messageLines = new List<string>();
        while (true)
        {
            var command = await ReadRequiredLineAsync(reader, cancellationToken).ConfigureAwait(false);
            RecordCommand(command);
            if (command.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250 2.1.0 accepted").ConfigureAwait(false);
                continue;
            }
            if (string.Equals(command, "DATA", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("354 end with <CRLF>.<CRLF>").ConfigureAwait(false);
                while (true)
                {
                    var line = await ReadRequiredLineAsync(reader, cancellationToken).ConfigureAwait(false);
                    if (line == ".") break;
                    messageLines.Add(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
                }
                LastMessage = string.Join("\r\n", messageLines) + "\r\n";
                Interlocked.Increment(ref _messageCount);
                if (_scenario == ScriptedSmtpScenario.DisconnectAfterData) return;
                await writer.WriteLineAsync("250 2.0.0 queued as scripted-ack").ConfigureAwait(false);
                continue;
            }
            if (string.Equals(command, "QUIT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("221 2.0.0 closing connection").ConfigureAwait(false);
                return;
            }
            await writer.WriteLineAsync("502 command not implemented").ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadRequiredLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken) =>
        await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ??
        throw new EndOfStreamException("The SMTP peer closed the scripted session.");

    private void RecordCommand(string command)
    {
        lock (_commands) _commands.Add(command);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature |
            X509KeyUsageFlags.KeyEncipherment |
            X509KeyUsageFlags.KeyCertSign,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        // Windows SChannel server authentication requires a persisted private-key
        // handle. This round trip does not install or trust the certificate.
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or ObjectDisposedException or SocketException or
            IOException or EndOfStreamException)
        {
        }
        _lifetime.Dispose();
        Certificate.Dispose();
    }
}
