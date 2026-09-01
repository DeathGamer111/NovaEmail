using MimeKit;

namespace NovaEmail.Mail;

public interface ISecureMailTransport
{
    Task<string> SendAsync(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        MimeMessage message,
        CancellationToken cancellationToken = default);

    Task<string> SendAsync(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        MimeMessage message,
        MailboxAddress envelopeSender,
        IReadOnlyCollection<MailboxAddress> envelopeRecipients,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteMessageDescriptor>> InspectImapInboxAsync(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        int maximumCount,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteMessageDescriptor>> InspectPopInboxAsync(
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        int maximumCount,
        CancellationToken cancellationToken = default);
}
