using System.Text;
using System.Globalization;
using MimeKit;

namespace NovaEmail.Mail.Tests;

public sealed class OutboundMessageComposerTests
{
    [Fact]
    public async Task BuildsStableInteroperableMimeWithThreadingAndAttachment()
    {
        var draft = new OutboundDraft(
            "operation-123",
            MailboxAddress.Parse("sender@example.test"),
            [MailboxAddress.Parse("receiver@example.test")],
            [], [], "Résumé reply", "plain body", "<p>html body</p>",
            [new OutboundAttachment("report.txt", "text/plain", Encoding.UTF8.GetBytes("attachment"))],
            "<parent@example.test>", ["root@example.test", "parent@example.test"],
            RequestReadReceipt: true,
            Priority: MessagePriority.Urgent);
        var first = OutboundMessageComposer.Compose(
            draft, DateTimeOffset.Parse("2026-08-13T17:00:00Z", CultureInfo.InvariantCulture));
        var second = OutboundMessageComposer.Compose(
            draft, DateTimeOffset.Parse("2026-08-13T17:01:00Z", CultureInfo.InvariantCulture));

        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Equal("parent@example.test", first.InReplyTo);
        Assert.Equal(["root@example.test", "parent@example.test"], first.References);
        Assert.Equal(MessagePriority.Urgent, first.Priority);
        await using var bytes = new MemoryStream();
        await first.WriteToAsync(bytes);
        bytes.Position = 0;
        var reparsed = await MimeMessage.LoadAsync(bytes);
        Assert.Equal("Résumé reply", reparsed.Subject);
        Assert.Equal("plain body", reparsed.TextBody);
        Assert.Equal("<p>html body</p>", reparsed.HtmlBody);
        Assert.Single(reparsed.Attachments);
    }

    [Fact]
    public void RejectsAttachmentPaths()
    {
        var baseDraft = new OutboundDraft(
            "operation", MailboxAddress.Parse("sender@example.test"),
            [MailboxAddress.Parse("receiver@example.test")], [], [], "subject", "body", null, []);
        Assert.Throws<InvalidDataException>(() => OutboundMessageComposer.Compose(
            baseDraft with { Attachments = [new OutboundAttachment("..\\secret.txt", "text/plain", new byte[] { 1 })] },
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EnforcesOutboundRecipientHeaderThreadingAndEntityLimits()
    {
        var sender = MailboxAddress.Parse("sender@example.test");
        var recipient = MailboxAddress.Parse("receiver@example.test");
        var baseline = new OutboundDraft(
            "bounded", sender, [recipient], [], [], "subject", "body", null, []);

        Assert.Throws<InvalidDataException>(() => OutboundMessageComposer.Compose(
            baseline with { To = Enumerable.Repeat(recipient, 101).ToArray() },
            DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => OutboundMessageComposer.Compose(
            baseline with { Subject = new string('s', 999) }, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => OutboundMessageComposer.Compose(
            baseline with { Priority = (MessagePriority)123 }, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => OutboundMessageComposer.Compose(
            baseline with { References = Enumerable.Repeat("id@example.test", 101).ToArray() },
            DateTimeOffset.UtcNow));
        Assert.Throws<InvalidDataException>(() => OutboundMessageComposer.Compose(
            baseline with
            {
                Attachments = Enumerable.Range(0, 2_001)
                    .Select(index => new OutboundAttachment(
                        $"{index}.txt", "text/plain", ReadOnlyMemory<byte>.Empty))
                    .ToArray(),
            }, DateTimeOffset.UtcNow));
    }
}
