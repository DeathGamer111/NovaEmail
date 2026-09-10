using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using MailKit;
using MailKit.Net.Imap;
using NovaEmail.Mail;
using NovaEmail.Safety;
using NovaEmail.Storage;
using MailKitAuthenticationException = MailKit.Security.AuthenticationException;
using SystemAuthenticationException = System.Security.Authentication.AuthenticationException;

namespace NovaEmail.Sync;

public sealed class MailKitImapMutationSink : IRemoteImapMutationSink
{
    private readonly SecureMailEndpoint _endpoint;
    private readonly MailCredentials _credentials;
    private readonly EndpointPolicy _endpointPolicy;
    private readonly CertificateTrustPolicy _certificateTrustPolicy;
    private readonly int _timeoutMilliseconds;
    private readonly Func<string, string?> _remoteFolderResolver;

    public MailKitImapMutationSink(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        EndpointPolicy endpointPolicy,
        CertificateTrustPolicy certificateTrustPolicy,
        int timeoutMilliseconds = 30_000,
        Func<string, string?>? remoteFolderResolver = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _endpointPolicy = endpointPolicy ?? throw new ArgumentNullException(nameof(endpointPolicy));
        _certificateTrustPolicy = certificateTrustPolicy ??
            throw new ArgumentNullException(nameof(certificateTrustPolicy));
        if (timeoutMilliseconds is < 100 or > 120_000)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        _timeoutMilliseconds = timeoutMilliseconds;
        _remoteFolderResolver = remoteFolderResolver ?? (folderId => folderId);
    }

    public async Task<RemoteMailboxMutationAcknowledgement> ApplyAsync(
        StoredMailboxMutationOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.ExpectedUid is null || operation.ExpectedUid is 0 or > uint.MaxValue ||
            string.IsNullOrWhiteSpace(operation.ExpectedUidValidity))
            throw Conflict("InvalidRemoteIdentity", "The queued action has no usable IMAP identity.");
        var sourceFullName = ResolveRemoteFullName(operation.SourceFolderId);
        var targetFullName = operation.Kind is MailboxMutationKind.MoveToFolder or
            MailboxMutationKind.MoveToDeletedItems
            ? ResolveRemoteFullName(operation.TargetFolderId ?? string.Empty)
            : null;

        using var client = new ImapClient
        {
            SslProtocols = EndpointPolicy.AllowedTlsProtocols,
            ServerCertificateValidationCallback = _certificateTrustPolicy.Validate,
            Timeout = _timeoutMilliseconds,
        };
        var mutationCommandMayHaveBeenSent = false;
        var copyCompleted = false;
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

            var source = await ResolveFolderAsync(client, sourceFullName, cancellationToken)
                .ConfigureAwait(false);
            await source.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            var expectedValidity = uint.Parse(operation.ExpectedUidValidity, NumberStyles.None, CultureInfo.InvariantCulture);
            if (source.UidValidity != expectedValidity)
                throw Conflict(
                    "UidValidityChanged",
                    $"IMAP UIDVALIDITY changed from {expectedValidity} to {source.UidValidity}; resynchronization is required.");

            var uid = new UniqueId((uint)operation.ExpectedUid.Value);
            var summaries = await source.FetchAsync(
                [uid], MessageSummaryItems.UniqueId, cancellationToken).ConfigureAwait(false);
            if (summaries.Count != 1 || summaries[0].UniqueId != uid)
                throw Conflict("RemoteUidMissing", "The queued IMAP UID no longer exists in its source folder.");

            string acknowledgement;
            switch (operation.Kind)
            {
                case MailboxMutationKind.SetFlag:
                case MailboxMutationKind.ClearFlag:
                    var (standardFlags, keywords) = ParseFlag(operation.Flag);
                    mutationCommandMayHaveBeenSent = true;
                    if (operation.Kind == MailboxMutationKind.SetFlag)
                    {
                        await source.AddFlagsAsync(
                            uid, standardFlags, keywords, silent: true, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await source.RemoveFlagsAsync(
                            uid, standardFlags, keywords, silent: true, cancellationToken).ConfigureAwait(false);
                    }
                    acknowledgement = string.Create(
                        CultureInfo.InvariantCulture,
                        $"imap-store:{source.UidValidity}:{uid.Id}:{operation.Kind}:{operation.Flag}");
                    break;
                case MailboxMutationKind.MoveToFolder:
                case MailboxMutationKind.MoveToDeletedItems:
                    if (string.IsNullOrWhiteSpace(operation.TargetFolderId))
                        throw Conflict("MissingTargetFolder", "The queued move has no target folder.");
                    var target = await ResolveFolderAsync(client, targetFullName!, cancellationToken)
                        .ConfigureAwait(false);
                    mutationCommandMayHaveBeenSent = true;
                    await source.CopyToAsync(uid, target, cancellationToken).ConfigureAwait(false);
                    copyCompleted = true;
                    await source.AddFlagsAsync(
                        uid, MessageFlags.Deleted, silent: true, cancellationToken).ConfigureAwait(false);
                    acknowledgement = string.Create(
                        CultureInfo.InvariantCulture,
                        $"imap-copy-delete-marker:{source.UidValidity}:{uid.Id}:{operation.TargetFolderId}");
                    break;
                case MailboxMutationKind.RetainDeletedHistory:
                    mutationCommandMayHaveBeenSent = true;
                    await source.AddFlagsAsync(
                        uid, MessageFlags.Deleted, silent: true, cancellationToken).ConfigureAwait(false);
                    acknowledgement = string.Create(
                        CultureInfo.InvariantCulture,
                        $"imap-deleted-marker-retained:{source.UidValidity}:{uid.Id}");
                    break;
                default:
                    throw Conflict("UnsupportedMutationKind", "The mailbox mutation kind is not supported.");
            }

            await DisconnectWithoutExpungeAsync(client).ConfigureAwait(false);
            return new RemoteMailboxMutationAcknowledgement(acknowledgement);
        }
        catch (RemoteMailboxMutationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is MailKitAuthenticationException or MailKit.Security.SslHandshakeException or
                SystemAuthenticationException)
        {
            throw Conflict(
                "TlsOrAuthenticationRejected",
                "The IMAP TLS or authentication boundary rejected the connection.", exception);
        }
        catch (FolderNotFoundException exception)
        {
            throw Conflict("RemoteFolderUnavailable", "A required pre-existing IMAP folder was not found.", exception);
        }
        catch (ImapCommandException exception)
        {
            throw Conflict(
                copyCompleted ? "MovePartiallyApplied" : "ImapCommandRejected",
                copyCompleted
                    ? "The message was copied, but the source deletion marker was not confirmed; manual reconciliation is required."
                    : "The IMAP server rejected the mutation command.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or ImapProtocolException or
            ServiceNotConnectedException or OperationCanceledException)
        {
            if (mutationCommandMayHaveBeenSent)
            {
                throw Conflict(
                    copyCompleted ? "MovePartiallyApplied" : "MutationOutcomeUnknown",
                    copyCompleted
                        ? "The copy completed, but the source marker outcome is unknown; automatic retry is prohibited."
                        : "The IMAP mutation outcome is unknown; automatic retry is prohibited.",
                    exception);
            }
            throw Retry(
                "MutationNotStarted",
                "The IMAP mutation command was not started and may be retried explicitly.",
                exception);
        }
        finally
        {
            if (client.IsConnected)
                await DisconnectWithoutExpungeAsync(client).ConfigureAwait(false);
        }
    }

    private string ResolveRemoteFullName(string folderId)
    {
        var fullName = _remoteFolderResolver(folderId);
        if (fullName is null)
            throw Conflict(
                "RemoteFolderNotBound",
                "The protected local folder is not bound to a pre-existing IMAP folder.");
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > 512 ||
            fullName.Any(char.IsControl))
            throw Conflict("InvalidRemoteFolder", "The IMAP folder name is invalid.");
        return fullName;
    }

    private static async Task<IMailFolder> ResolveFolderAsync(
        ImapClient client,
        string fullName,
        CancellationToken cancellationToken)
    {
        return fullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(fullName, cancellationToken).ConfigureAwait(false);
    }

    private static (MessageFlags StandardFlags, HashSet<string> Keywords) ParseFlag(string? flag)
    {
        if (string.IsNullOrWhiteSpace(flag) || flag.Length > 255 || flag.Any(char.IsControl))
            throw Conflict("InvalidRemoteFlag", "The queued IMAP flag is invalid.");
        var standard = flag switch
        {
            "\\Seen" => MessageFlags.Seen,
            "\\Answered" => MessageFlags.Answered,
            "\\Flagged" => MessageFlags.Flagged,
            "\\Draft" => MessageFlags.Draft,
            "\\Deleted" or "\\Recent" => throw Conflict(
                "ReservedRemoteFlag", "Direct mutation of the reserved IMAP flag is prohibited."),
            _ when flag.StartsWith('\\') => throw Conflict(
                "UnsupportedSystemFlag", "The queued IMAP system flag is not supported."),
            _ => MessageFlags.None,
        };
        return (standard, standard == MessageFlags.None ? new HashSet<string>([flag], StringComparer.Ordinal) : []);
    }

    private static void EnsureModernTls(SslProtocols protocol)
    {
        if (protocol is not SslProtocols.Tls12 and not SslProtocols.Tls13)
            throw new SystemAuthenticationException($"IMAP negotiated prohibited TLS protocol '{protocol}'.");
    }

    private static async Task DisconnectWithoutExpungeAsync(ImapClient client)
    {
        if (!client.IsConnected) return;
        try
        {
            // LOGOUT does not implicitly expunge a selected mailbox. Deliberately do
            // not call CloseAsync, MoveToAsync, or ExpungeAsync from this boundary.
            await client.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or ImapProtocolException or ServiceNotConnectedException)
        {
        }
    }

    private static RemoteMailboxMutationException Retry(
        string code,
        string message,
        Exception? exception = null) =>
        new(RemoteMailboxMutationDisposition.RetryScheduled, code, message, exception);

    private static RemoteMailboxMutationException Conflict(
        string code,
        string message,
        Exception? exception = null) =>
        new(RemoteMailboxMutationDisposition.Conflict, code, message, exception);
}
