using System.Text;
using System.Globalization;
using MimeKit;

namespace NovaEmail.Mail.Tests;

public sealed class ConversationDraftFactoryTests
{
    [Fact]
    public void ReplyAndReplyAllPreserveThreadingWithoutCopyingBccOrOwnAddress()
    {
        var source = NewSource();
        source.ReplyTo.Add(MailboxAddress.Parse("helpdesk@example.test"));
        source.To.Add(MailboxAddress.Parse("me@example.test"));
        source.To.Add(MailboxAddress.Parse("team@example.test"));
        source.Cc.Add(MailboxAddress.Parse("other@example.test"));
        source.Cc.Add(MailboxAddress.Parse("team@example.test"));
        source.Bcc.Add(MailboxAddress.Parse("hidden@example.test"));
        source.References.Add("root@example.test");
        var sender = MailboxAddress.Parse("me@example.test");

        var reply = ConversationDraftFactory.Create(source, sender, ConversationDraftKind.Reply);
        var replyAll = ConversationDraftFactory.Create(source, sender, ConversationDraftKind.ReplyAll);

        Assert.Equal("Re: Status", reply.Subject);
        Assert.Equal("helpdesk@example.test", Assert.Single(reply.To.Mailboxes).Address);
        Assert.Empty(reply.Cc);
        Assert.Equal("source@example.test", reply.InReplyTo);
        Assert.Equal(["root@example.test", "source@example.test"], reply.References);
        Assert.Contains("> Original body.", reply.TextBody, StringComparison.Ordinal);
        Assert.Equal(
            ["team@example.test", "other@example.test"],
            replyAll.Cc.Mailboxes.Select(address => address.Address));
        Assert.Empty(replyAll.Bcc);
        Assert.DoesNotContain("hidden@example.test", replyAll.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForwardAttachesCanonicalOriginalButStripsBccFromForwardedCopy()
    {
        var source = NewSource();
        source.To.Add(MailboxAddress.Parse("recipient@example.test"));
        source.Bcc.Add(MailboxAddress.Parse("hidden@example.test"));
        var originalAttachment = new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream(new byte[] { 1, 2, 3 }, writable: false)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            FileName = "data.bin",
        };
        source.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Original body." },
            originalAttachment,
        };

        var forward = ConversationDraftFactory.Create(
            source, MailboxAddress.Parse("me@example.test"), ConversationDraftKind.Forward);

        Assert.Equal("Fwd: Status", forward.Subject);
        Assert.Empty(forward.To);
        var attached = Assert.IsType<MessagePart>(Assert.Single(forward.Attachments));
        Assert.NotNull(attached.Message);
        Assert.Equal("forwarded-message.eml", attached.ContentDisposition?.FileName);
        Assert.Empty(attached.Message!.Bcc);
        Assert.Equal("recipient@example.test", Assert.Single(attached.Message.To.Mailboxes).Address);
        Assert.Single(attached.Message.Attachments);
        await using var bytes = new MemoryStream();
        await attached.Message.WriteToAsync(bytes);
        Assert.DoesNotContain("hidden@example.test", Encoding.ASCII.GetString(bytes.ToArray()),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hidden@example.test", source.Bcc.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepeatedRepliesKeepStableOrderedReferenceChain()
    {
        var sender = MailboxAddress.Parse("me@example.test");
        var source = NewSource();
        source.To.Add(sender);
        var first = ConversationDraftFactory.Create(source, sender, ConversationDraftKind.Reply);
        first.MessageId = "first-reply@example.test";
        first.From.Clear();
        first.From.Add(MailboxAddress.Parse("original@example.test"));

        var second = ConversationDraftFactory.Create(first, sender, ConversationDraftKind.Reply);

        Assert.Equal(
            ["source@example.test", "first-reply@example.test"],
            second.References);
        Assert.Equal("first-reply@example.test", second.InReplyTo);
        Assert.Equal("Re: Status", second.Subject);
    }

    private static MimeMessage NewSource()
    {
        var source = new MimeMessage
        {
            MessageId = "source@example.test",
            Subject = "Status",
            Date = DateTimeOffset.Parse("2026-08-13T17:00:00Z", CultureInfo.InvariantCulture),
            Body = new TextPart("plain") { Text = "Original body." },
        };
        source.From.Add(MailboxAddress.Parse("original@example.test"));
        return source;
    }
}
