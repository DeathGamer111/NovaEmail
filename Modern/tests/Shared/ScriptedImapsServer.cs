using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NovaEmail.Testing;

internal enum ScriptedImapScenario
{
    Accept,
    DisconnectAfterUidStore,
    FolderCatalog,
    OversizedFolderCatalogEntry,
}

/// <summary>
/// A single-session, loopback-only implicit-TLS IMAP double. It implements the
/// minimal authenticated folder, UID FETCH, UID STORE, and UID COPY commands
/// needed to prove mutation behavior without a Docker or production server.
/// </summary>
internal sealed class ScriptedImapsServer : IAsyncDisposable
{
    private static readonly Encoding WireEncoding = new UTF8Encoding(false, true);
    private readonly TcpListener _listener;
    private readonly ScriptedImapScenario _scenario;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _serverTask;
    private readonly List<string> _commands = [];
    private int _connectionCount;

    private ScriptedImapsServer(ScriptedImapScenario scenario)
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
    public Exception? LastServerException { get; private set; }
    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_commands) return _commands.ToArray();
        }
    }

    public static ScriptedImapsServer Start(ScriptedImapScenario scenario) => new(scenario);

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

        using var reader = new StreamReader(
            tls, WireEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        await using var writer = new StreamWriter(
            tls, WireEncoding, bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n",
        };
        await writer.WriteLineAsync(
            "* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN NAMESPACE UIDPLUS UNSELECT] NovaEmail scripted IMAP ready")
            .ConfigureAwait(false);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) return;
            var firstSpace = line.IndexOf(' ');
            if (firstSpace <= 0)
            {
                await writer.WriteLineAsync("* BAD malformed command").ConfigureAwait(false);
                continue;
            }
            var tag = line[..firstSpace];
            var remainder = line[(firstSpace + 1)..];
            var commandEnd = remainder.IndexOf(' ');
            var command = (commandEnd < 0 ? remainder : remainder[..commandEnd]).ToUpperInvariant();
            RecordCommand(SanitizeCommand(command, line));

            switch (command)
            {
                case "CAPABILITY":
                    await writer.WriteLineAsync(
                        "* CAPABILITY IMAP4rev1 AUTH=PLAIN NAMESPACE UIDPLUS UNSELECT")
                        .ConfigureAwait(false);
                    await writer.WriteLineAsync($"{tag} OK capability complete").ConfigureAwait(false);
                    break;
                case "AUTHENTICATE":
                    await writer.WriteLineAsync("+").ConfigureAwait(false);
                    _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync($"{tag} OK authentication complete").ConfigureAwait(false);
                    break;
                case "LOGIN":
                    await writer.WriteLineAsync($"{tag} OK login complete").ConfigureAwait(false);
                    break;
                case "NAMESPACE":
                    await writer.WriteLineAsync("* NAMESPACE ((\"\" \"/\")) NIL NIL").ConfigureAwait(false);
                    await writer.WriteLineAsync($"{tag} OK namespace complete").ConfigureAwait(false);
                    break;
                case "LIST":
                    if (line.Contains("\"*\"", StringComparison.Ordinal) ||
                        line.Contains("\"%\"", StringComparison.Ordinal))
                    {
                        if (_scenario == ScriptedImapScenario.OversizedFolderCatalogEntry)
                        {
                            await writer.WriteLineAsync(
                                $"* LIST (\\HasNoChildren) \"/\" \"{new string('A', 513)}\"")
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await writer.WriteLineAsync(
                                "* LIST (\\Inbox \\Subscribed \\HasNoChildren) \"/\" \"INBOX\"")
                                .ConfigureAwait(false);
                            await writer.WriteLineAsync(
                                "* LIST (\\Archive \\HasNoChildren) \"/\" \"Archive\"")
                                .ConfigureAwait(false);
                            await writer.WriteLineAsync(
                                "* LIST (\\NoSelect \\HasChildren) \"/\" \"Projects\"")
                                .ConfigureAwait(false);
                            await writer.WriteLineAsync(
                                "* LIST (\\HasNoChildren) \"/\" \"Projects/2026\"")
                                .ConfigureAwait(false);
                        }
                        await writer.WriteLineAsync($"{tag} OK catalog complete").ConfigureAwait(false);
                        break;
                    }
                    var listedFolder = line.Contains("Archive", StringComparison.OrdinalIgnoreCase)
                        ? "Archive"
                        : line.Contains("DeletedItems", StringComparison.OrdinalIgnoreCase)
                            ? "DeletedItems"
                            : "INBOX";
                    await writer.WriteLineAsync($"* LIST () \"/\" \"{listedFolder}\"").ConfigureAwait(false);
                    await writer.WriteLineAsync($"{tag} OK list complete").ConfigureAwait(false);
                    break;
                case "SELECT":
                    await writer.WriteLineAsync("* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)")
                        .ConfigureAwait(false);
                    await writer.WriteLineAsync(
                        "* OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft \\*)] flags")
                        .ConfigureAwait(false);
                    await writer.WriteLineAsync("* 1 EXISTS").ConfigureAwait(false);
                    await writer.WriteLineAsync("* 0 RECENT").ConfigureAwait(false);
                    await writer.WriteLineAsync("* OK [UIDVALIDITY 17] validity").ConfigureAwait(false);
                    await writer.WriteLineAsync("* OK [UIDNEXT 32] next").ConfigureAwait(false);
                    await writer.WriteLineAsync($"{tag} OK [READ-WRITE] select complete").ConfigureAwait(false);
                    break;
                case "UID":
                    if (remainder.StartsWith("UID FETCH ", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("* 1 FETCH (UID 31)").ConfigureAwait(false);
                        await writer.WriteLineAsync($"{tag} OK fetch complete").ConfigureAwait(false);
                        break;
                    }
                    if (remainder.StartsWith("UID COPY ", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync($"{tag} OK [COPYUID 17 31 81] copy complete")
                            .ConfigureAwait(false);
                        break;
                    }
                    if (remainder.StartsWith("UID STORE ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_scenario == ScriptedImapScenario.DisconnectAfterUidStore) return;
                        await writer.WriteLineAsync($"{tag} OK store complete").ConfigureAwait(false);
                        break;
                    }
                    await writer.WriteLineAsync($"{tag} BAD unsupported UID command").ConfigureAwait(false);
                    break;
                case "UNSELECT":
                    await writer.WriteLineAsync($"{tag} OK unselect complete").ConfigureAwait(false);
                    break;
                case "NOOP":
                    await writer.WriteLineAsync($"{tag} OK noop complete").ConfigureAwait(false);
                    break;
                case "LOGOUT":
                    await writer.WriteLineAsync("* BYE logging out").ConfigureAwait(false);
                    await writer.WriteLineAsync($"{tag} OK logout complete").ConfigureAwait(false);
                    return;
                default:
                    await writer.WriteLineAsync($"{tag} BAD command not implemented").ConfigureAwait(false);
                    break;
            }
        }
    }

    private static string SanitizeCommand(string command, string line) =>
        command is "LOGIN" or "AUTHENTICATE" ? $"{line.Split(' ', 2)[0]} {command} <redacted>" : line;

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
