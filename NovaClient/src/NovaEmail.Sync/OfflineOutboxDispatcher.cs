using MimeKit;
using NovaEmail.Mail;
using NovaEmail.Storage;

namespace NovaEmail.Sync;

public sealed record OutboundDispatchResult(
    string OperationId,
    OperationEventState State,
    string? Detail);

public sealed class OfflineOutboxDispatcher
{
    private const string InvalidQueuedMimeDetail =
        "Queued MIME failed immutable validation; inspect the local operation error code.";
    private const string SmtpAcknowledgedDetail =
        "SMTP server acknowledgement recorded; response text was redacted.";
    private const string SubmissionNotStartedDetail =
        "SMTP submission did not start and may be retried only by an explicit request.";
    private const string SubmissionOutcomeUnknownDetail =
        "SMTP submission outcome is unknown; automatic retry is prohibited.";
    private readonly ModernMailStore _store;
    private readonly ISecureMailTransport _transport;

    public OfflineOutboxDispatcher(ModernMailStore store, ISecureMailTransport transport)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<OutboundDispatchResult?> DispatchNextAsync(
        string accountId,
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        await _store.MarkInterruptedOutboundAttemptsAsConflictsAsync(cancellationToken).ConfigureAwait(false);
        var queued = (await _store.ReadReadyOutboundMessagesAsync(
                accountId, 1, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault();
        if (queued is null) return null;
        if (!queued.AccountId.Equals(accountId, StringComparison.Ordinal))
            throw new InvalidDataException("Ready outbound work escaped its immutable account boundary.");
        await _store.AppendOperationEventAsync(
            queued.OperationId, OperationEventState.Attempting, cancellationToken: cancellationToken).ConfigureAwait(false);
        MimeMessage message;
        MailboxAddress? envelopeSender = null;
        MailboxAddress[]? envelopeRecipients = null;
        MessageAttachmentImport[] attachments;
        try
        {
            var rawMime = await _store.ReadOutboundMimeAsync(queued.OperationId, cancellationToken).ConfigureAwait(false);
            message = await SafeMimeParser.ParseAsync(rawMime, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    message.MessageId?.Trim().Trim('<', '>'),
                    queued.MessageId.Trim().Trim('<', '>'),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Queued MIME Message-ID differs from its immutable outbox record.");
            if (queued.EnvelopeRecipients.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(queued.EnvelopeSender))
                    throw new InvalidDataException("Queued explicit SMTP envelope has no sender.");
                envelopeSender = MailboxAddress.Parse(queued.EnvelopeSender);
                envelopeRecipients = queued.EnvelopeRecipients
                    .Select(MailboxAddress.Parse)
                    .ToArray();
                if (envelopeRecipients.Length is 0 or > 100)
                    throw new InvalidDataException("Queued explicit SMTP envelope recipient count is invalid.");
            }
            else if (!string.IsNullOrEmpty(queued.EnvelopeSender))
            {
                throw new InvalidDataException("Queued SMTP envelope sender has no recipients.");
            }
            var materialized = await SafeAttachmentExporter.MaterializeAsync(
                message, cancellationToken: cancellationToken).ConfigureAwait(false);
            attachments = materialized.Select(attachment => new MessageAttachmentImport(
                attachment.Ordinal,
                attachment.FileName,
                attachment.MediaType,
                attachment.Content,
                attachment.ContentId)).ToArray();
        }
        catch (OperationCanceledException)
        {
            await _store.AppendOperationEventAsync(
                queued.OperationId, OperationEventState.RetryScheduled, errorCode: "CanceledBeforeSubmission",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await _store.AppendOperationEventAsync(
                queued.OperationId, OperationEventState.Failed, errorCode: "InvalidQueuedMime",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new OutboundDispatchResult(
                queued.OperationId, OperationEventState.Failed, InvalidQueuedMimeDetail);
        }

        try
        {
            var acknowledgement = envelopeSender is null || envelopeRecipients is null
                ? await _transport.SendAsync(
                    endpoint, credentials, message, cancellationToken).ConfigureAwait(false)
                : await _transport.SendAsync(
                    endpoint, credentials, message, envelopeSender, envelopeRecipients,
                    cancellationToken).ConfigureAwait(false);
            _ = acknowledgement;
            var recipients = envelopeRecipients is null
                ? string.Join(", ", message.To.Concat(message.Cc).Select(address => address.ToString()))
                : string.Join(", ", envelopeRecipients.Select(address => address.ToString()));
            await _store.AcknowledgeOutboundAndProjectSentAsync(
                queued.OperationId,
                new SentMessageProjectionInput(
                    message.MessageId!,
                    message.Subject ?? string.Empty,
                    envelopeSender?.ToString() ?? message.From.ToString(),
                    recipients,
                    message.Date == DateTimeOffset.MinValue ? null : message.Date.ToUniversalTime(),
                    message.TextBody,
                    message.HtmlBody,
                    attachments),
                SmtpAcknowledgedDetail,
                CancellationToken.None).ConfigureAwait(false);
            return new OutboundDispatchResult(
                queued.OperationId, OperationEventState.Acknowledged, SmtpAcknowledgedDetail);
        }
        catch (MailDeliveryException exception)
        {
            var state = exception.SubmissionCertainty == MailSubmissionCertainty.NotSubmitted
                ? OperationEventState.RetryScheduled
                : OperationEventState.Conflict;
            var code = exception.SubmissionCertainty == MailSubmissionCertainty.NotSubmitted
                ? "SubmissionNotStarted"
                : "SubmissionOutcomeUnknown";
            await _store.AppendOperationEventAsync(
                queued.OperationId, state, errorCode: code, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            return new OutboundDispatchResult(
                queued.OperationId,
                state,
                exception.SubmissionCertainty == MailSubmissionCertainty.NotSubmitted
                    ? SubmissionNotStartedDetail
                    : SubmissionOutcomeUnknownDetail);
        }
        catch (OperationCanceledException)
        {
            await _store.AppendOperationEventAsync(
                queued.OperationId, OperationEventState.Conflict, errorCode: "CanceledSubmissionOutcomeUnknown",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
