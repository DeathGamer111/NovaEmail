using System.Text;
using System.Text.Json;
using MimeKit;
using NovaEmail.Storage;

namespace NovaEmail.Sync.Tests;

public sealed class SynchronizationTests
{
    [Fact]
    public async Task UidValidityResetReconcilesIdenticalMimeWithoutDuplication()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var engine = new ImapSyncEngine(store);
            var raw = CreateMime("stable-id@example.test", "preserved");
            var first = await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(RemoteMessageChangeKind.Upsert, 1, raw, ["Seen"])], true)));
            var reset = await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("200", 9,
                    [new RemoteMessageChange(RemoteMessageChangeKind.Upsert, 9, raw, ["Seen", "Flagged"])], true)));

            Assert.Equal(1, first.Imported);
            Assert.True(reset.UidValidityChanged);
            Assert.Equal(1, reset.Reconciled);
            Assert.Equal(1, await store.GetMessageCountAsync());
            var cursor = await store.GetSyncCursorAsync("account", "inbox");
            Assert.Equal("200", cursor!.UidValidity);
            Assert.Equal(9UL, cursor.HighestUid);
            Assert.Equal([9UL], await store.GetRemoteUidsAsync("account", "inbox", "200"));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteSameValidityReplayDoesNotMoveCursorBackward()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var engine = new ImapSyncEngine(store);
            var first = new RemoteMailboxSnapshot("100", 2,
            [
                new RemoteMessageChange(RemoteMessageChangeKind.Upsert, 1, CreateMime("one@example.test", "one"), []),
                new RemoteMessageChange(RemoteMessageChangeKind.Upsert, 2, CreateMime("two@example.test", "two"), []),
            ], true);
            await engine.SynchronizeAsync("account", "inbox", new FakeRemote(first));
            var replay = await engine.SynchronizeAsync("account", "inbox", new FakeRemote(first));

            Assert.Equal(2, replay.Reconciled);
            Assert.Equal(0, replay.Imported);
            Assert.Equal(2UL, (await store.GetSyncCursorAsync("account", "inbox"))!.HighestUid);
            Assert.Equal(2, await store.GetMessageCountAsync());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SameValidityFlagSnapshotUpdatesFolderStateWithoutMimeBytes()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var engine = new ImapSyncEngine(store);
            await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(
                        RemoteMessageChangeKind.Upsert, 1,
                        CreateMime("flags@example.test", "body"), ["\\Seen"])], true)));

            var result = await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(
                        RemoteMessageChangeKind.FlagsOnly, 1, null,
                        ["project-keyword", "\\Flagged"])], true)));

            Assert.Equal(1, result.Reconciled);
            Assert.Equal(0, result.Imported);
            Assert.Empty(result.Conflicts);
            Assert.Equal(
                ["\\Flagged", "project-keyword"],
                await store.GetRemoteMessageFlagsAsync("account", "inbox", "100", 1));
            Assert.Equal(1, await store.GetMessageCountAsync());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FlagsOnlySnapshotWithoutLocalIdentityRemainsVisibleAsConflict()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var engine = new ImapSyncEngine(store);
            var result = await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(RemoteMessageChangeKind.FlagsOnly, 1, null, ["\\Seen"])], true)));

            Assert.Equal(0, result.Reconciled);
            Assert.Single(result.Conflicts);
            Assert.Contains("no matching local identity", result.Conflicts[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HostileFlagSetIsRejectedBeforeReplacingKnownState()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var engine = new ImapSyncEngine(store);
            await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(
                        RemoteMessageChangeKind.Upsert, 1,
                        CreateMime("flag-limit@example.test", "body"), ["\\Seen"])], true)));
            var oversizedFlags = Enumerable.Range(0, 129).Select(index => $"flag-{index}").ToArray();

            await Assert.ThrowsAsync<InvalidDataException>(() => engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(RemoteMessageChangeKind.FlagsOnly, 1, null, oversizedFlags)], true))));

            Assert.Equal(["\\Seen"],
                await store.GetRemoteMessageFlagsAsync("account", "inbox", "100", 1));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FlagsAreScopedToFolderWhenMimeBytesMatch()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var engine = new ImapSyncEngine(store);
            var raw = CreateMime("folder-flags@example.test", "shared bytes");
            await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(RemoteMessageChangeKind.Upsert, 1, raw, ["\\Seen"])], true)));
            await engine.SynchronizeAsync(
                "account", "archive",
                new FakeRemote(new RemoteMailboxSnapshot("200", 7,
                    [new RemoteMessageChange(RemoteMessageChangeKind.Upsert, 7, raw, ["\\Flagged"])], true)));

            Assert.Equal(["\\Seen"],
                await store.GetRemoteMessageFlagsAsync("account", "inbox", "100", 1));
            Assert.Equal(["\\Flagged"],
                await store.GetRemoteMessageFlagsAsync("account", "archive", "200", 7));
            Assert.Equal(2, await store.GetMessageCountAsync());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UidValidityResetRequiresCompleteSnapshot()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            await store.SaveSyncCursorAsync(new StoredSyncCursor("account", "inbox", "100", 7, DateTimeOffset.UtcNow));
            var engine = new ImapSyncEngine(store);
            await Assert.ThrowsAsync<InvalidDataException>(() => engine.SynchronizeAsync(
                "account", "inbox", new FakeRemote(new RemoteMailboxSnapshot("200", 0, [], false))));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UidValidityResetTombstonesAbsentOldGenerationWithoutPurgingHistory()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var engine = new ImapSyncEngine(store);
            var retained = CreateMime("retained@example.test", "retained");
            await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 2,
                [
                    new RemoteMessageChange(RemoteMessageChangeKind.Upsert, 1, retained, []),
                    new RemoteMessageChange(
                        RemoteMessageChangeKind.Upsert, 2,
                        CreateMime("removed-after-reset@example.test", "preserved"), []),
                ], true)));

            var reset = await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("200", 9,
                    [new RemoteMessageChange(RemoteMessageChangeKind.Upsert, 9, retained, [])], true)));

            Assert.True(reset.UidValidityChanged);
            Assert.Equal(2, reset.Tombstoned);
            Assert.Equal(2, await store.GetMessageCountAsync());
            var folder = Assert.Single(await store.ReadStoredFolderSummariesAsync());
            Assert.Equal(1, folder.ActiveMessageCount);
            Assert.Equal(1, folder.TombstonedMessageCount);
            Assert.Equal([9UL], await store.GetRemoteUidsAsync("account", "inbox", "200"));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptedUidValidityResetDoesNotAdvanceCursor()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var engine = new ImapSyncEngine(store);
            await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(
                        RemoteMessageChangeKind.Upsert, 1,
                        CreateMime("old@example.test", "old"), [])], true)));

            await Assert.ThrowsAsync<InvalidDataException>(() => engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("200", 10,
                [
                    new RemoteMessageChange(
                        RemoteMessageChangeKind.Upsert, 9,
                        CreateMime("new@example.test", "new"), []),
                    new RemoteMessageChange(RemoteMessageChangeKind.Upsert, 10, [], []),
                ], true))));

            var interruptedCursor = await store.GetSyncCursorAsync("account", "inbox");
            Assert.Equal("100", interruptedCursor!.UidValidity);
            Assert.Equal(1UL, interruptedCursor.HighestUid);

            var retry = await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("200", 9,
                    [new RemoteMessageChange(
                        RemoteMessageChangeKind.Upsert, 9,
                        CreateMime("new@example.test", "new"), [])], true)));
            Assert.True(retry.UidValidityChanged);
            Assert.Equal("200", (await store.GetSyncCursorAsync("account", "inbox"))!.UidValidity);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RetriedUidValidityResetTombstonesPartialNewGenerationOmissionsWithoutPurgingMime()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var engine = new ImapSyncEngine(store);
            await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(
                        RemoteMessageChangeKind.Upsert, 1,
                        CreateMime("old-before-reset@example.test", "old generation"), [])], true)));

            var partialMime = CreateMime("partial-reset@example.test", "retained partial MIME");
            var partial = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account", "inbox", "99", "200", null,
                "partial-reset@example.test", "Partial reset import",
                "sender@example.test", "receiver@example.test", null,
                partialMime, "retained partial MIME", null, []));

            var retry = await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("200", 1,
                    [new RemoteMessageChange(
                        RemoteMessageChangeKind.Upsert, 1,
                        CreateMime("current-after-reset@example.test", "current generation"), [])], true)));

            Assert.True(retry.UidValidityChanged);
            Assert.Equal(2, retry.Tombstoned);
            Assert.Equal([1UL], await store.GetRemoteUidsAsync("account", "inbox", "200"));
            Assert.Equal(partialMime, await store.ReadStoredMimeAsync(partial.MessageId));
            Assert.Equal(3, await store.GetMessageCountAsync());
            var folder = Assert.Single(await store.ReadStoredFolderSummariesAsync());
            Assert.Equal(1, folder.ActiveMessageCount);
            Assert.Equal(2, folder.TombstonedMessageCount);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RetriedInitialSyncTombstonesPreCursorOmissionsWithoutPurgingMime()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var partialMime = CreateMime("initial-partial@example.test", "retained before first cursor");
            var partial = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account", "inbox", "22", "100", null,
                "initial-partial@example.test", "Initial partial import",
                "sender@example.test", "receiver@example.test", null,
                partialMime, "retained before first cursor", null, []));
            Assert.Null(await store.GetSyncCursorAsync("account", "inbox"));

            var result = await new ImapSyncEngine(store).SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(
                        RemoteMessageChangeKind.Upsert, 1,
                        CreateMime("initial-current@example.test", "current first snapshot"), [])], true)));

            Assert.False(result.UidValidityChanged);
            Assert.Equal(1, result.Tombstoned);
            Assert.Equal([1UL], await store.GetRemoteUidsAsync("account", "inbox", "100"));
            Assert.Equal(partialMime, await store.ReadStoredMimeAsync(partial.MessageId));
            var folder = Assert.Single(await store.ReadStoredFolderSummariesAsync());
            Assert.Equal(1, folder.ActiveMessageCount);
            Assert.Equal(1, folder.TombstonedMessageCount);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RemoteUidLookupCannotBorrowActiveStateFromAnotherFolder()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var raw = CreateMime("shared-folder-state@example.test", "shared folder state");
            await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account", "inbox", "7", "100", null,
                "shared-folder-state@example.test", "Shared folder state",
                "sender@example.test", "receiver@example.test", null,
                raw, "shared folder state", null, []));
            await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account", "archive", "8", "200", null,
                "shared-folder-state@example.test", "Shared folder state",
                "sender@example.test", "receiver@example.test", null,
                raw, "shared folder state", null, []));

            Assert.True(await store.TombstoneRemoteMessageAsync("account", "inbox", "100", 7));

            Assert.Empty(await store.GetRemoteUidsAsync("account", "inbox", "100"));
            Assert.Equal([8UL], await store.GetRemoteUidsAsync("account", "archive", "200"));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteSnapshotInfersMissingUidTombstone()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var engine = new ImapSyncEngine(store);
            await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(
                        RemoteMessageChangeKind.Upsert, 1,
                        CreateMime("missing@example.test", "preserved"), [])], true)));

            var result = await engine.SynchronizeAsync(
                "account", "inbox", new FakeRemote(new RemoteMailboxSnapshot("100", 1, [], true)));

            Assert.Equal(1, result.Tombstoned);
            Assert.Equal(1, await store.GetMessageCountAsync());
            var folder = Assert.Single(await store.ReadStoredFolderSummariesAsync());
            Assert.Equal(0, folder.ActiveMessageCount);
            Assert.Equal(1, folder.TombstonedMessageCount);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ServerRemovalCreatesTombstoneAndNeverPurges()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var engine = new ImapSyncEngine(store);
            await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(RemoteMessageChangeKind.Upsert, 1, CreateMime("remove@example.test", "body"), [])], true)));
            var result = await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(RemoteMessageChangeKind.Removed, 1, null, [])], false)));

            Assert.Equal(1, result.Tombstoned);
            Assert.Empty(result.Conflicts);
            Assert.Equal(1, await store.GetMessageCountAsync());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ImapImportPersistsSanitizedHashVerifiedAttachmentContent()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var engine = new ImapSyncEngine(store);
            var attachmentBytes = "exact IMAP attachment"u8.ToArray();
            var raw = CreateMimeWithAttachment(
                "imap-attachment@example.test", "../../imap-evidence.txt", attachmentBytes);

            var result = await engine.SynchronizeAsync(
                "account", "inbox",
                new FakeRemote(new RemoteMailboxSnapshot("100", 1,
                    [new RemoteMessageChange(RemoteMessageChangeKind.Upsert, 1, raw, ["\\Seen"])], true)));

            Assert.Equal(1, result.Imported);
            var storedMessage = Assert.Single((await store.ListStoredMessagesPageAsync(10)).Messages);
            var attachment = Assert.Single(
                await store.ReadStoredAttachmentSummariesAsync(storedMessage.MessageId));
            Assert.Equal("imap-evidence.txt", attachment.FileName);
            Assert.Equal(attachmentBytes, await store.ReadStoredAttachmentContentAsync(
                storedMessage.MessageId, attachment.Ordinal));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static byte[] CreateMime(string messageId, string body) => Encoding.ASCII.GetBytes(
        $"From: sender@example.test\r\nTo: receiver@example.test\r\nSubject: Sync test\r\nMessage-ID: <{messageId}>\r\nDate: Thu, 13 Aug 2026 12:00:00 -0500\r\n\r\n{body}\r\n");

    private static byte[] CreateMimeWithAttachment(
        string messageId,
        string fileName,
        byte[] attachmentBytes)
    {
        var builder = new BodyBuilder { TextBody = "Attachment import test." };
        builder.Attachments.Add(
            fileName, attachmentBytes, ContentType.Parse("application/octet-stream"));
        var message = new MimeMessage
        {
            From = { MailboxAddress.Parse("sender@example.test") },
            To = { MailboxAddress.Parse("receiver@example.test") },
            Subject = "Sync attachment test",
            MessageId = messageId,
            Date = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.FromHours(-5)),
            Body = builder.ToMessageBody(),
        };
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        options.EnsureNewLine = true;
        using var output = new MemoryStream();
        message.WriteTo(options, output);
        return output.ToArray();
    }

    private static async Task<ModernMailStore> CreateStoreAsync(string testRoot)
    {
        var data = Path.Combine(testRoot, "data");
        var store = new ModernMailStore(Path.Combine(data, "mail.db"), data);
        await store.InitializeAsync();
        return store;
    }

    private static string CreateTestRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var root = Path.Combine(repositoryRoot, ".artifacts", "sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Modern")) &&
                Directory.Exists(Path.Combine(directory.FullName, "TestLab"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private sealed class FakeRemote(RemoteMailboxSnapshot snapshot) : IRemoteImapSnapshotSource
    {
        public Task<RemoteMailboxSnapshot> GetSnapshotAsync(
            RemoteSyncCheckpoint checkpoint,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

}
