using System.Text;
using MimeKit;

namespace NovaEmail.Mail;

public enum ConversationDraftKind
{
    Reply,
    ReplyAll,
    Forward,
}

public static class ConversationDraftFactory
{
    private const int MaximumRecipients = 100;
    private const int MaximumReferences = 100;
    private const int MaximumQuotedCharacters = 10 * 1024 * 1024;

    public static MimeMessage Create(
        MimeMessage source,
        MailboxAddress sender,
        ConversationDraftKind kind)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sender);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        SafeMimeParser.ValidateMessage(source);

        var draft = new MimeMessage { Subject = BuildSubject(source.Subject, kind) };
        draft.From.Add(sender);
        if (kind == ConversationDraftKind.Forward)
        {
            draft.Body = BuildForwardBody(source);
            return draft;
        }

        var replyTargets = (source.ReplyTo.Mailboxes.Any()
                ? source.ReplyTo.Mailboxes
                : source.From.Mailboxes)
            .Where(address => !SameMailbox(address, sender))
            .Distinct(MailboxAddressComparer.Instance)
            .ToArray();
        if (replyTargets.Length == 0)
            throw new InvalidDataException("The source message has no reply recipient other than the selected sender.");
        draft.To.AddRange(replyTargets);

        if (kind == ConversationDraftKind.ReplyAll)
        {
            var excluded = replyTargets.Append(sender).ToHashSet(MailboxAddressComparer.Instance);
            var copied = source.To.Mailboxes.Concat(source.Cc.Mailboxes)
                .Where(address => !excluded.Contains(address))
                .Distinct(MailboxAddressComparer.Instance)
                .ToArray();
            draft.Cc.AddRange(copied);
        }
        if (draft.To.Mailboxes.Count() + draft.Cc.Mailboxes.Count() > MaximumRecipients)
            throw new InvalidDataException("Reply recipients exceed the 100-recipient outbound limit.");

        if (!string.IsNullOrWhiteSpace(source.MessageId))
            draft.InReplyTo = NormalizeMessageId(source.MessageId);
        var references = source.References
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeMessageId)
            .Append(string.IsNullOrWhiteSpace(source.MessageId)
                ? null
                : NormalizeMessageId(source.MessageId))
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(MaximumReferences)
            .ToArray();
        foreach (var reference in references) draft.References.Add(reference);
        draft.Body = new TextPart("plain") { Text = BuildReplyText(source) };
        return draft;
    }

    private static Multipart BuildForwardBody(MimeMessage source)
    {
        var retainedHeaders = source.Headers
            .Where(header => header.Id != HeaderId.Bcc)
            .Select(header => new Header(header.Field, header.Value))
            .ToArray();
        var retained = new MimeMessage((IEnumerable<Header>)retainedHeaders) { Body = source.Body };
        var messagePart = new MessagePart("rfc822")
        {
            Message = retained,
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
        };
        messagePart.ContentDisposition.FileName = "forwarded-message.eml";
        return new Multipart("mixed")
        {
            new TextPart("plain")
            {
                Text = "Forwarded message is attached in standards-compatible RFC/MIME form.\r\n",
            },
            messagePart,
        };
    }

    private static string BuildReplyText(MimeMessage source)
    {
        var originalText = source.TextBody ??
            "[The original message has no plain-text alternative. Its retained source remains unchanged.]";
        if (originalText.Length > MaximumQuotedCharacters)
            throw new InvalidDataException("The original plain-text body exceeds the reply quotation limit.");
        var normalized = originalText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var quoted = new StringBuilder(normalized.Length + 256);
        quoted.Append("\r\n\r\nOn ")
            .Append(source.Date == DateTimeOffset.MinValue ? "an unknown date" : source.Date.ToString("u"))
            .Append(", ")
            .Append(source.From)
            .Append(" wrote:\r\n");
        foreach (var line in normalized.Split('\n')) quoted.Append("> ").Append(line).Append("\r\n");
        return quoted.ToString();
    }

    private static string BuildSubject(string? sourceSubject, ConversationDraftKind kind)
    {
        var subject = sourceSubject ?? string.Empty;
        return kind switch
        {
            ConversationDraftKind.Reply or ConversationDraftKind.ReplyAll
                when subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) => subject,
            ConversationDraftKind.Reply or ConversationDraftKind.ReplyAll => "Re: " + subject,
            ConversationDraftKind.Forward
                when subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ||
                     subject.StartsWith("Fw:", StringComparison.OrdinalIgnoreCase) => subject,
            ConversationDraftKind.Forward => "Fwd: " + subject,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static string NormalizeMessageId(string value) => value.Trim().Trim('<', '>');

    private static bool SameMailbox(MailboxAddress left, MailboxAddress right) =>
        left.Address.Equals(right.Address, StringComparison.OrdinalIgnoreCase);

    private sealed class MailboxAddressComparer : IEqualityComparer<MailboxAddress>
    {
        public static MailboxAddressComparer Instance { get; } = new();

        public bool Equals(MailboxAddress? left, MailboxAddress? right) =>
            left is not null && right is not null && SameMailbox(left, right);

        public int GetHashCode(MailboxAddress address) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(address.Address);
    }
}
