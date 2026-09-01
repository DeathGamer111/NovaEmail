using System.Security.Authentication;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MimeKit;
using NovaEmail.Safety;

namespace NovaEmail.Mail;

public sealed class SecureMailTransport : ISecureMailTransport
{
    private const int MinimumTimeoutMilliseconds = 100;
    private const int MaximumTimeoutMilliseconds = 120_000;
    private readonly EndpointPolicy _endpointPolicy;
    private readonly CertificateTrustPolicy _certificateTrustPolicy;
    private readonly int _timeoutMilliseconds;

    public SecureMailTransport(
        EndpointPolicy endpointPolicy,
        CertificateTrustPolicy certificateTrustPolicy,
        int timeoutMilliseconds = 30_000)
    {
        _endpointPolicy = endpointPolicy ?? throw new ArgumentNullException(nameof(endpointPolicy));
        _certificateTrustPolicy = certificateTrustPolicy ??
            throw new ArgumentNullException(nameof(certificateTrustPolicy));
        if (timeoutMilliseconds is < MinimumTimeoutMilliseconds or > MaximumTimeoutMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMilliseconds),
                $"Transport timeout must be between {MinimumTimeoutMilliseconds:N0} and " +
                $"{MaximumTimeoutMilliseconds:N0} milliseconds.");
        }
        _timeoutMilliseconds = timeoutMilliseconds;
    }

    public async Task<string> SendAsync(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        MimeMessage message,
        CancellationToken cancellationToken = default)
        => await SendCoreAsync(
            endpoint, credentials, message, envelopeSender: null, envelopeRecipients: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<string> SendAsync(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        MimeMessage message,
        MailboxAddress envelopeSender,
        IReadOnlyCollection<MailboxAddress> envelopeRecipients,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopeSender);
        ArgumentNullException.ThrowIfNull(envelopeRecipients);
        if (envelopeRecipients.Count is 0 or > 100)
            throw new ArgumentOutOfRangeException(
                nameof(envelopeRecipients), "SMTP envelope must contain between 1 and 100 recipients.");
        return await SendCoreAsync(
            endpoint, credentials, message, envelopeSender, envelopeRecipients,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendCoreAsync(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        MimeMessage message,
        MailboxAddress? envelopeSender,
        IReadOnlyCollection<MailboxAddress>? envelopeRecipients,
        CancellationToken cancellationToken)
    {
        ValidateArguments(endpoint, credentials);
        ArgumentNullException.ThrowIfNull(message);

        using var client = new SmtpClient();
        Configure(client);
        try
        {
            var socket = await _endpointPolicy.ConnectAllowedAsync(
                endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
            await client.ConnectAsync(
                socket,
                endpoint.Host,
                endpoint.Port,
                endpoint.ToSecureSocketOptions(),
                cancellationToken).ConfigureAwait(false);
            EnsureModernTls(client.SslProtocol);
            await client.AuthenticateAsync(
                credentials.UserName,
                credentials.Password,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MailDeliveryException(
                MailSubmissionCertainty.NotSubmitted,
                "SMTP connection, TLS validation, or authentication failed before submission.",
                exception);
        }

        try
        {
            _ = envelopeSender is null || envelopeRecipients is null
                ? await client.SendAsync(message, cancellationToken).ConfigureAwait(false)
                : await client.SendAsync(
                    message, envelopeSender, envelopeRecipients, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new MailDeliveryException(
                MailSubmissionCertainty.Unknown,
                "SMTP submission did not produce a reliable acknowledgement; automatic retry is blocked.",
                exception);
        }
        try
        {
            await client.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The server already acknowledged the message. A disconnect failure must not trigger resubmission.
        }
        // SMTP response text is controlled by the server. Do not return it to a
        // caller that may journal or display it because an authentication peer
        // could reflect credential material in that text.
        return "smtp-server-acknowledged-response-redacted";
    }

    public async Task<MimeMessage> DownloadImapMessageAsync(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        string uniqueId,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(endpoint, credentials);
        if (!uint.TryParse(
                uniqueId, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var uid) || uid == 0)
            throw new ArgumentException("IMAP unique ID is invalid.", nameof(uniqueId));

        using var client = new ImapClient();
        Configure(client);
        var socket = await _endpointPolicy.ConnectAllowedAsync(
            endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
        await client.ConnectAsync(
            socket, endpoint.Host, endpoint.Port, endpoint.ToSecureSocketOptions(),
            cancellationToken).ConfigureAwait(false);
        EnsureModernTls(client.SslProtocol);
        await client.AuthenticateAsync(
            credentials.UserName, credentials.Password, cancellationToken).ConfigureAwait(false);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        await using var source = await client.Inbox.GetStreamAsync(
            new UniqueId(uid), cancellationToken).ConfigureAwait(false);
        var message = await SafeMimeParser.ParseAsync(source, cancellationToken).ConfigureAwait(false);
        await client.Inbox.CloseAsync(expunge: false, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        return message;
    }

    public async Task<IReadOnlyList<RemoteMessageDescriptor>> InspectImapInboxAsync(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(endpoint, credentials);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);

        using var client = new ImapClient();
        Configure(client);
        var socket = await _endpointPolicy.ConnectAllowedAsync(
            endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
        await client.ConnectAsync(
            socket,
            endpoint.Host,
            endpoint.Port,
            endpoint.ToSecureSocketOptions(),
            cancellationToken).ConfigureAwait(false);
        EnsureModernTls(client.SslProtocol);
        await client.AuthenticateAsync(
            credentials.UserName,
            credentials.Password,
            cancellationToken).ConfigureAwait(false);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

        var startIndex = Math.Max(0, client.Inbox.Count - maximumCount);
        var descriptors = new List<RemoteMessageDescriptor>();
        if (client.Inbox.Count > 0)
        {
            var summaries = await client.Inbox.FetchAsync(
                startIndex,
                -1,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope,
                cancellationToken).ConfigureAwait(false);
            descriptors.AddRange(summaries.Select(summary => new RemoteMessageDescriptor(
                summary.UniqueId.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                summary.Envelope?.MessageId,
                summary.Envelope?.Subject ?? string.Empty,
                summary.Envelope?.From?.ToString() ?? string.Empty)));
        }

        await client.Inbox.CloseAsync(expunge: false, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        return descriptors;
    }

    public async Task<IReadOnlyList<RemoteMessageDescriptor>> InspectPopInboxAsync(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(endpoint, credentials);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);

        using var client = new Pop3Client();
        Configure(client);
        var socket = await _endpointPolicy.ConnectAllowedAsync(
            endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
        await client.ConnectAsync(
            socket,
            endpoint.Host,
            endpoint.Port,
            endpoint.ToSecureSocketOptions(),
            cancellationToken).ConfigureAwait(false);
        EnsureModernTls(client.SslProtocol);
        await client.AuthenticateAsync(
            credentials.UserName,
            credentials.Password,
            cancellationToken).ConfigureAwait(false);

        var startIndex = Math.Max(0, client.Count - maximumCount);
        var descriptors = new List<RemoteMessageDescriptor>();
        for (var index = startIndex; index < client.Count; index++)
        {
            var uid = await client.GetMessageUidAsync(index, cancellationToken).ConfigureAwait(false);
            var headers = await client.GetMessageHeadersAsync(index, cancellationToken).ConfigureAwait(false);
            descriptors.Add(new RemoteMessageDescriptor(
                uid,
                headers[HeaderId.MessageId],
                headers[HeaderId.Subject] ?? string.Empty,
                headers[HeaderId.From] ?? string.Empty));
        }

        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        return descriptors;
    }

    private static void ValidateArguments(SecureMailEndpoint endpoint, MailCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.UserName);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.Password);
    }

    private void Configure(MailService client)
    {
        client.SslProtocols = EndpointPolicy.AllowedTlsProtocols;
        client.ServerCertificateValidationCallback = _certificateTrustPolicy.Validate;
        client.Timeout = _timeoutMilliseconds;
    }

    private static void EnsureModernTls(SslProtocols protocol)
    {
        if (protocol is not SslProtocols.Tls12 and not SslProtocols.Tls13)
        {
            throw new System.Security.Authentication.AuthenticationException(
                $"Mail connection negotiated prohibited TLS protocol '{protocol}'.");
        }
    }
}
