using System.Globalization;
using System.Text;
using MimeKit;
using NovaEmail.Storage;

namespace NovaEmail.Mail.Tests;

public sealed class LocalDemoOutboxStagingTests
{
    [Fact]
    public async Task ComposeAndReplySendStageExactThreadedMimeInLocalOutboxWithoutDispatchEligibility()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(), $"novaemail-local-demo-staging-{Guid.NewGuid():N}");
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();

            var compose = OutboundMessageComposer.Compose(
                new OutboundDraft(
                    "demo-compose",
                    MailboxAddress.Parse("demo-author@novaemail.test"),
                    [MailboxAddress.Parse("compose-recipient@novaemail.test")],
                    [], [],
                    "Local demo compose",
                    "Compose plain body",
                    "<p>Compose HTML body</p>",
                    [new OutboundAttachment(
                        "agenda.txt", "text/plain", Encoding.UTF8.GetBytes("Demo agenda"))]),
                DateTimeOffset.Parse(
                    "2026-08-31T15:00:00Z", CultureInfo.InvariantCulture));
            var reply = OutboundMessageComposer.Compose(
                new OutboundDraft(
                    "demo-reply",
                    MailboxAddress.Parse("demo-author@novaemail.test"),
                    [MailboxAddress.Parse("reply-recipient@novaemail.test")],
                    [], [],
                    "Re: Local demo thread",
                    "Reply plain body",
                    null,
                    [],
                    InReplyTo: "parent@novaemail.test",
                    References: ["root@novaemail.test", "parent@novaemail.test"]),
                DateTimeOffset.Parse(
                    "2026-08-31T15:05:00Z", CultureInfo.InvariantCulture));
            var composeMime = await SerializeAsync(compose);
            var replyMime = await SerializeAsync(reply);

            var composeQueue = await SaveAndStageAsync(store, compose, composeMime);
            var replyQueue = await SaveAndStageAsync(store, reply, replyMime);

            Assert.Equal(composeMime, await store.ReadOutboundMimeAsync(composeQueue.OperationId));
            Assert.Equal(replyMime, await store.ReadOutboundMimeAsync(replyQueue.OperationId));
            var operations = await store.ReadOutboundOperationSummariesAsync();
            Assert.Equal(2, operations.Count);
            Assert.All(operations, operation =>
            {
                Assert.Equal(MailAccountProfilePolicy.LocalOutboxStagingAccountId, operation.AccountId);
                Assert.Equal(OperationEventState.Queued, operation.State);
                Assert.StartsWith("draft-revision:", operation.IdempotencyKey, StringComparison.Ordinal);
            });

            var stagedCompose = await ParseAsync(
                await store.ReadOutboundMimeAsync(composeQueue.OperationId));
            Assert.Equal("Local demo compose", stagedCompose.Subject);
            Assert.Equal("Compose plain body", stagedCompose.TextBody);
            Assert.Equal("<p>Compose HTML body</p>", stagedCompose.HtmlBody);
            var attachment = Assert.Single(stagedCompose.Attachments);
            Assert.Equal("agenda.txt", attachment.ContentDisposition?.FileName);

            var stagedReply = await ParseAsync(
                await store.ReadOutboundMimeAsync(replyQueue.OperationId));
            Assert.Equal("Re: Local demo thread", stagedReply.Subject);
            Assert.Equal("Reply plain body", stagedReply.TextBody?.TrimEnd('\r', '\n'));
            Assert.Equal("parent@novaemail.test", stagedReply.InReplyTo);
            Assert.Equal(
                ["root@novaemail.test", "parent@novaemail.test"],
                stagedReply.References);
            Assert.Empty(await store.ReadStoredFolderSummariesAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ReadReadyOutboundMessagesAsync(
                    MailAccountProfilePolicy.LocalOutboxStagingAccountId, 10));

            var reopened = new ModernMailStore(database, dataRoot);
            await reopened.InitializeAsync();
            Assert.Equal(composeMime, await reopened.ReadOutboundMimeAsync(composeQueue.OperationId));
            Assert.Equal(replyMime, await reopened.ReadOutboundMimeAsync(replyQueue.OperationId));
            Assert.Equal(2, (await reopened.ReadOutboundOperationSummariesAsync()).Count);
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<QueuedOutboundMessage> SaveAndStageAsync(
        ModernMailStore store,
        MimeMessage message,
        byte[] rawMime)
    {
        var saved = await store.SaveDraftRevisionAsync(new DraftRevisionImport(
            DraftId: null,
            rawMime,
            message.Subject ?? string.Empty,
            message.From.ToString(),
            message.To.ToString(),
            message.TextBody ?? string.Empty));
        var storedDraftMime = await store.ReadDraftMimeAsync(saved.DraftId, saved.Revision);
        Assert.Equal(rawMime, storedDraftMime);
        return await store.QueueOutboundMessageAsync(
            MailAccountProfilePolicy.LocalOutboxStagingAccountId,
            $"draft-revision:{saved.DraftId}:{saved.Revision}:{saved.RawMimeSha256}",
            message.MessageId ?? throw new InvalidDataException("Composed message lacks a Message-ID."),
            storedDraftMime);
    }

    private static async Task<byte[]> SerializeAsync(MimeMessage message)
    {
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        options.EnsureNewLine = true;
        await using var stream = new MemoryStream();
        await message.WriteToAsync(options, stream);
        return stream.ToArray();
    }

    private static async Task<MimeMessage> ParseAsync(byte[] rawMime)
    {
        await using var stream = new MemoryStream(rawMime, writable: false);
        return await MimeMessage.LoadAsync(stream);
    }
}
