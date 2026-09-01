using System.Net.Sockets;
using System.Security.Authentication;
using MailKit;
using MailKit.Net.Imap;
using NovaEmail.Mail;
using NovaEmail.Safety;
using MailKitAuthenticationException = MailKit.Security.AuthenticationException;
using SystemAuthenticationException = System.Security.Authentication.AuthenticationException;

namespace NovaEmail.Sync;

public sealed record RemoteImapFolderCatalogEntry(
    string FullName,
    string Name,
    char DirectorySeparator,
    bool IsSelectable,
    bool IsInbox,
    bool IsSubscribed,
    bool HasChildren,
    IReadOnlyList<string> SpecialUses);

public sealed class MailKitImapFolderCatalog
{
    private const int MaximumNamespaces = 16;
    private const int MaximumFolders = 10_000;
    private const int MaximumFolderNameCharacters = 512;
    private readonly SecureMailEndpoint _endpoint;
    private readonly MailCredentials _credentials;
    private readonly EndpointPolicy _endpointPolicy;
    private readonly CertificateTrustPolicy _certificateTrustPolicy;
    private readonly int _timeoutMilliseconds;

    public MailKitImapFolderCatalog(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        EndpointPolicy endpointPolicy,
        CertificateTrustPolicy certificateTrustPolicy,
        int timeoutMilliseconds = 30_000)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _endpointPolicy = endpointPolicy ?? throw new ArgumentNullException(nameof(endpointPolicy));
        _certificateTrustPolicy = certificateTrustPolicy ??
            throw new ArgumentNullException(nameof(certificateTrustPolicy));
        if (timeoutMilliseconds is < 100 or > 120_000)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        _timeoutMilliseconds = timeoutMilliseconds;
    }

    public async Task<IReadOnlyList<RemoteImapFolderCatalogEntry>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        using var client = new ImapClient
        {
            SslProtocols = EndpointPolicy.AllowedTlsProtocols,
            ServerCertificateValidationCallback = _certificateTrustPolicy.Validate,
            Timeout = _timeoutMilliseconds,
        };
        try
        {
            var socket = await _endpointPolicy.ConnectAllowedAsync(
                _endpoint.Host, _endpoint.Port, cancellationToken).ConfigureAwait(false);
            await client.ConnectAsync(
                socket,
                _endpoint.Host,
                _endpoint.Port,
                _endpoint.ToSecureSocketOptions(),
                cancellationToken).ConfigureAwait(false);
            EnsureModernTls(client.SslProtocol);
            await client.AuthenticateAsync(
                _credentials.UserName, _credentials.Password, cancellationToken).ConfigureAwait(false);
            if (client.PersonalNamespaces.Count is 0 or > MaximumNamespaces)
                throw new InvalidDataException("IMAP personal namespace count is invalid or exceeds its limit.");

            var entries = new Dictionary<string, RemoteImapFolderCatalogEntry>(StringComparer.Ordinal);
            foreach (var folderNamespace in client.PersonalNamespaces)
            {
                var folders = await client.GetFoldersAsync(
                    folderNamespace,
                    StatusItems.None,
                    subscribedOnly: false,
                    cancellationToken).ConfigureAwait(false);
                foreach (var folder in folders)
                {
                    var entry = CreateEntry(folder);
                    if (!entries.TryAdd(entry.FullName, entry) && entries[entry.FullName] != entry)
                        throw new InvalidDataException("IMAP server returned conflicting folder definitions.");
                    if (entries.Count > MaximumFolders)
                        throw new InvalidDataException("IMAP folder catalog exceeds the configured limit.");
                }
            }

            await DisconnectAsync(client).ConfigureAwait(false);
            return entries.Values
                .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is MailKitAuthenticationException or MailKit.Security.SslHandshakeException or
                SystemAuthenticationException)
        {
            throw new SystemAuthenticationException(
                "The IMAP TLS or authentication boundary rejected folder discovery.", exception);
        }
        finally
        {
            if (client.IsConnected) await DisconnectAsync(client).ConfigureAwait(false);
        }
    }

    private static RemoteImapFolderCatalogEntry CreateEntry(IMailFolder folder)
    {
        if (string.IsNullOrWhiteSpace(folder.FullName) ||
            folder.FullName.Length > MaximumFolderNameCharacters ||
            folder.FullName.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(folder.Name) ||
            folder.Name.Length > MaximumFolderNameCharacters ||
            folder.Name.Any(char.IsControl))
            throw new InvalidDataException("IMAP server returned an invalid or oversized folder name.");
        var attributes = folder.Attributes;
        return new RemoteImapFolderCatalogEntry(
            folder.FullName,
            folder.Name,
            folder.DirectorySeparator,
            !attributes.HasFlag(FolderAttributes.NoSelect) &&
                !attributes.HasFlag(FolderAttributes.NonExistent),
            attributes.HasFlag(FolderAttributes.Inbox) ||
                folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase),
            attributes.HasFlag(FolderAttributes.Subscribed),
            attributes.HasFlag(FolderAttributes.HasChildren),
            GetSpecialUses(attributes));
    }

    private static List<string> GetSpecialUses(FolderAttributes attributes)
    {
        var uses = new List<string>();
        if (attributes.HasFlag(FolderAttributes.All)) uses.Add("All");
        if (attributes.HasFlag(FolderAttributes.Archive)) uses.Add("Archive");
        if (attributes.HasFlag(FolderAttributes.Drafts)) uses.Add("Drafts");
        if (attributes.HasFlag(FolderAttributes.Flagged)) uses.Add("Flagged");
        if (attributes.HasFlag(FolderAttributes.Important)) uses.Add("Important");
        if (attributes.HasFlag(FolderAttributes.Junk)) uses.Add("Junk");
        if (attributes.HasFlag(FolderAttributes.Sent)) uses.Add("Sent");
        if (attributes.HasFlag(FolderAttributes.Trash)) uses.Add("Trash");
        return uses;
    }

    private static void EnsureModernTls(SslProtocols protocol)
    {
        if (protocol is not SslProtocols.Tls12 and not SslProtocols.Tls13)
            throw new SystemAuthenticationException($"IMAP negotiated prohibited TLS protocol '{protocol}'.");
    }

    private static async Task DisconnectAsync(ImapClient client)
    {
        if (!client.IsConnected) return;
        try
        {
            await client.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or ImapProtocolException or ServiceNotConnectedException)
        {
        }
    }
}
