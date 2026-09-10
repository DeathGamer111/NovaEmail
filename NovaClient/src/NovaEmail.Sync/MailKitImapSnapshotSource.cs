using System.Security.Authentication;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using NovaEmail.Mail;
using NovaEmail.Safety;

namespace NovaEmail.Sync;

public sealed class MailKitImapSnapshotSource : IRemoteImapSnapshotSource
{
    private const int MaximumMessageBytes = 100 * 1024 * 1024;
    private const int MaximumRemoteChanges = 100_000;
    private const int MaximumRemoteFlags = 128;
    private const int MaximumRemoteFlagCharacters = 255;
    private readonly SecureMailEndpoint _endpoint;
    private readonly MailCredentials _credentials;
    private readonly string _folderName;
    private readonly EndpointPolicy _endpointPolicy;
    private readonly CertificateTrustPolicy _certificateTrustPolicy;
    private readonly IProtocolLogger? _protocolLogger;

    public MailKitImapSnapshotSource(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        string folderName,
        EndpointPolicy endpointPolicy,
        CertificateTrustPolicy certificateTrustPolicy)
        : this(
            endpoint,
            credentials,
            folderName,
            endpointPolicy,
            certificateTrustPolicy,
            protocolLogger: null)
    {
    }

    internal MailKitImapSnapshotSource(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        string folderName,
        EndpointPolicy endpointPolicy,
        CertificateTrustPolicy certificateTrustPolicy,
        IProtocolLogger? protocolLogger)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _folderName = string.IsNullOrWhiteSpace(folderName) ? "INBOX" : folderName;
        _endpointPolicy = endpointPolicy ?? throw new ArgumentNullException(nameof(endpointPolicy));
        _certificateTrustPolicy = certificateTrustPolicy ??
            throw new ArgumentNullException(nameof(certificateTrustPolicy));
        _protocolLogger = protocolLogger;
    }

    public async Task<RemoteMailboxSnapshot> GetSnapshotAsync(
        RemoteSyncCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(checkpoint.KnownUids);
        using var client = _protocolLogger is null
            ? new ImapClient()
            : new ImapClient(_protocolLogger);
        client.SslProtocols = EndpointPolicy.AllowedTlsProtocols;
        client.ServerCertificateValidationCallback = _certificateTrustPolicy.Validate;
        client.Timeout = 30_000;
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
        var folder = string.Equals(_folderName, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(_folderName, cancellationToken).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

        var uidValidity = folder.UidValidity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var uids = await folder.SearchAsync(SearchQuery.All, cancellationToken).ConfigureAwait(false);
        if (uids.Count > MaximumRemoteChanges || checkpoint.KnownUids.Count > MaximumRemoteChanges)
            throw new InvalidDataException("IMAP mailbox exceeds the configured synchronization change limit.");
        IList<IMessageSummary> summaries = uids.Count == 0
            ? []
            : await folder.FetchAsync(
                uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags,
                cancellationToken).ConfigureAwait(false);
        var changes = new List<RemoteMessageChange>(summaries.Count + checkpoint.KnownUids.Count);
        var sameUidValidity = string.Equals(checkpoint.UidValidity, uidValidity, StringComparison.Ordinal);
        foreach (var summary in summaries.OrderBy(summary => summary.UniqueId.Id))
        {
            if (sameUidValidity && checkpoint.KnownUids.Contains(summary.UniqueId.Id))
            {
                changes.Add(new RemoteMessageChange(
                    RemoteMessageChangeKind.FlagsOnly,
                    summary.UniqueId.Id,
                    null,
                    GetFlags(summary)));
                continue;
            }
            await using var source = await folder.GetStreamAsync(summary.UniqueId, cancellationToken)
                .ConfigureAwait(false);
            var rawMime = await ReadBoundedAsync(source, cancellationToken).ConfigureAwait(false);
            changes.Add(new RemoteMessageChange(
                RemoteMessageChangeKind.Upsert,
                summary.UniqueId.Id,
                rawMime,
                GetFlags(summary)));
        }

        if (sameUidValidity)
        {
            var remoteUids = uids.Select(uid => (ulong)uid.Id).ToHashSet();
            changes.AddRange(checkpoint.KnownUids
                .Where(uid => !remoteUids.Contains(uid))
                .Select(uid => new RemoteMessageChange(RemoteMessageChangeKind.Removed, uid, null, [])));
        }

        var currentHighestUid = uids.Count == 0 ? 0UL : uids.Max(uid => (ulong)uid.Id);
        var highestUid = sameUidValidity
            ? Math.Max(checkpoint.HighestKnownUid, currentHighestUid)
            : currentHighestUid;
        await folder.CloseAsync(expunge: false, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        return new RemoteMailboxSnapshot(uidValidity, highestUid, changes, IsCompleteSnapshot: true);
    }

    private static List<string> GetFlags(IMessageSummary summary)
    {
        var flags = new List<string>();
        var value = summary.Flags ?? MessageFlags.None;
        if (value.HasFlag(MessageFlags.Seen)) flags.Add("\\Seen");
        if (value.HasFlag(MessageFlags.Answered)) flags.Add("\\Answered");
        if (value.HasFlag(MessageFlags.Flagged)) flags.Add("\\Flagged");
        if (value.HasFlag(MessageFlags.Deleted)) flags.Add("\\Deleted");
        if (value.HasFlag(MessageFlags.Draft)) flags.Add("\\Draft");
        if (value.HasFlag(MessageFlags.Recent)) flags.Add("\\Recent");
        flags.AddRange(summary.Keywords.Order(StringComparer.Ordinal));
        if (flags.Count > MaximumRemoteFlags ||
            flags.Any(flag => string.IsNullOrWhiteSpace(flag) ||
                flag.Length > MaximumRemoteFlagCharacters ||
                flag.Any(character => char.IsControl(character))))
            throw new InvalidDataException("IMAP message contains an invalid or oversized flag set.");
        return flags;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumMessageBytes)
                throw new InvalidDataException("IMAP message exceeds the configured size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static void EnsureModernTls(SslProtocols protocol)
    {
        if (protocol is not SslProtocols.Tls12 and not SslProtocols.Tls13)
            throw new System.Security.Authentication.AuthenticationException(
                $"IMAP connection negotiated prohibited TLS protocol '{protocol}'.");
    }
}
