using System.Security.Cryptography;
using System.Text;
using MimeKit;

namespace NovaEmail.Mail;

public sealed record OutboundAttachment(
    string FileName,
    string MediaType,
    ReadOnlyMemory<byte> Content);

public sealed record OutboundDraft(
    string IdempotencyKey,
    MailboxAddress From,
    IReadOnlyList<MailboxAddress> To,
    IReadOnlyList<MailboxAddress> Cc,
    IReadOnlyList<MailboxAddress> Bcc,
    string Subject,
    string? TextBody,
    string? HtmlBody,
    IReadOnlyList<OutboundAttachment> Attachments,
    string? InReplyTo = null,
    IReadOnlyList<string>? References = null,
    bool RequestReadReceipt = false,
    MessagePriority Priority = MessagePriority.Normal);

public static class OutboundMessageComposer
{
    private const int MaximumRecipients = 100;
    private const int MaximumAttachments = 2_000;
    private const int MaximumReferences = 100;
    private const long MaximumAttachmentBytes = 100L * 1024L * 1024L;

    public static MimeMessage Compose(OutboundDraft draft, DateTimeOffset composedUtc)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.IdempotencyKey);
        ArgumentNullException.ThrowIfNull(draft.From);
        var recipientCount = checked(draft.To.Count + draft.Cc.Count + draft.Bcc.Count);
        if (recipientCount == 0)
            throw new InvalidDataException("At least one outbound recipient is required.");
        if (recipientCount > MaximumRecipients)
            throw new InvalidDataException("Outbound message exceeds the 100-recipient limit.");
        if (draft.TextBody is null && draft.HtmlBody is null)
            throw new InvalidDataException("Outbound message has no body.");
        if ((draft.Subject?.Length ?? 0) > 998)
            throw new InvalidDataException("Outbound subject exceeds the configured header limit.");
        if (!Enum.IsDefined(draft.Priority))
            throw new InvalidDataException("Outbound message priority is invalid.");
        var message = new MimeMessage
        {
            MessageId = BuildStableMessageId(draft.IdempotencyKey),
            Subject = draft.Subject ?? string.Empty,
            Date = composedUtc.ToUniversalTime(),
            Priority = draft.Priority,
        };
        message.From.Add(draft.From);
        message.To.AddRange(draft.To);
        message.Cc.AddRange(draft.Cc);
        message.Bcc.AddRange(draft.Bcc);
        if (!string.IsNullOrWhiteSpace(draft.InReplyTo)) message.InReplyTo = NormalizeMessageId(draft.InReplyTo);
        if (draft.References is not null)
        {
            if (draft.References.Count > MaximumReferences)
                throw new InvalidDataException("Outbound threading references exceed the configured limit.");
            foreach (var reference in draft.References.Where(value => !string.IsNullOrWhiteSpace(value)))
                message.References.Add(NormalizeMessageId(reference));
        }
        if (draft.RequestReadReceipt)
        {
            message.Headers[HeaderId.DispositionNotificationTo] = draft.From.Address;
            message.Headers["Return-Receipt-To"] = draft.From.Address;
        }
        var body = new BodyBuilder { TextBody = draft.TextBody, HtmlBody = draft.HtmlBody };
        if (draft.Attachments.Count > MaximumAttachments)
            throw new InvalidDataException("Outbound message exceeds the configured attachment-count limit.");
        long totalAttachmentBytes = 0;
        foreach (var attachment in draft.Attachments)
        {
            ValidateFileName(attachment.FileName);
            totalAttachmentBytes = checked(totalAttachmentBytes + attachment.Content.Length);
            if (totalAttachmentBytes > MaximumAttachmentBytes)
                throw new InvalidDataException("Outbound attachments exceed the configured size limit.");
            var contentType = ContentType.TryParse(attachment.MediaType, out var parsed)
                ? parsed
                : new ContentType("application", "octet-stream");
            body.Attachments.Add(attachment.FileName, attachment.Content.ToArray(), contentType);
        }
        message.Body = body.ToMessageBody();
        SafeMimeParser.ValidateMessage(message);
        return message;
    }

    public static string BuildStableMessageId(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))
            .ToLowerInvariant();
        return $"nova-{digest}@local.novaemail.invalid";
    }

    private static string NormalizeMessageId(string value) => value.Trim().Trim('<', '>');

    private static void ValidateFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Attachment filename contains a path or prohibited characters.");
    }
}
