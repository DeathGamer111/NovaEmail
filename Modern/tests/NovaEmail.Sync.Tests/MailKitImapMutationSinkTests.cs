using System.Text;
using NovaEmail.Mail;
using NovaEmail.Safety;
using NovaEmail.Storage;
using NovaEmail.Testing;

namespace NovaEmail.Sync.Tests;

public sealed class MailKitImapMutationSinkTests
{
    [Fact]
    public async Task UnboundLocalFolderIsRejectedBeforeAnyConnectionAttempt()
    {
        var sink = new MailKitImapMutationSink(
            new SecureMailEndpoint("localhost", 1, TlsConnectionMode.ImplicitTls),
            new MailCredentials("unused", "unused"),
            new EndpointPolicy(),
            new CertificateTrustPolicy(),
            remoteFolderResolver: _ => null);
        var operation = new StoredMailboxMutationOperation(
            "operation", "key", "account-a", "message", "local-folder",
            MailboxMutationKind.SetFlag, null, "\\Seen", "17", 31,
            OperationEventState.Queued, DateTimeOffset.UtcNow, null, null, WasAlreadyPresent: false);

        var exception = await Assert.ThrowsAsync<RemoteMailboxMutationException>(() =>
            sink.ApplyAsync(operation));

        Assert.Equal(RemoteMailboxMutationDisposition.Conflict, exception.Disposition);
        Assert.Equal("RemoteFolderNotBound", exception.ErrorCode);
    }

    [Fact]
    public async Task RealMailKitFlagReplayUsesUidStoreAndNeverExpunges()
    {
        var root = CreateTestRoot();
        await using var server = ScriptedImapsServer.Start(ScriptedImapScenario.Accept);
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueFlagAsync(store, "real-imap-flag");
            var dispatcher = CreateDispatcher(store, server);

            var result = await dispatcher.DispatchNextAsync("account-a");
            Assert.Null(await dispatcher.DispatchNextAsync("account-a"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.WaitForCompletionAsync(timeout.Token);

            Assert.Equal(OperationEventState.Acknowledged, result!.State);
            Assert.Equal(OperationEventState.Acknowledged,
                await store.GetLatestOperationStateAsync(queued.OperationId));
            Assert.Equal(1, server.ConnectionCount);
            Assert.Contains(server.Commands, command =>
                command.Contains(" UID STORE 31 +FLAGS.SILENT", StringComparison.OrdinalIgnoreCase) &&
                command.Contains("\\Flagged", StringComparison.Ordinal));
            AssertNoPermanentRemovalCommands(server.Commands);
            Assert.DoesNotContain(server.Commands, command =>
                command.Contains("local-password", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RealMailKitMoveUsesCopyThenDeletedMarkerWithoutMoveOrExpunge()
    {
        var root = CreateTestRoot();
        await using var server = ScriptedImapsServer.Start(ScriptedImapScenario.Accept);
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueMoveAsync(store, "real-imap-move");
            var dispatcher = CreateDispatcher(store, server);

            var result = await dispatcher.DispatchNextAsync("account-a");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.WaitForCompletionAsync(timeout.Token);

            Assert.Equal(OperationEventState.Acknowledged, result!.State);
            Assert.Equal(OperationEventState.Acknowledged,
                await store.GetLatestOperationStateAsync(queued.OperationId));
            var copyIndex = FindCommand(server.Commands, " UID COPY 31 ");
            var markerIndex = FindCommand(server.Commands, " UID STORE 31 +FLAGS.SILENT");
            Assert.True(copyIndex >= 0, "The server did not observe UID COPY.");
            Assert.True(markerIndex > copyIndex, "The deletion marker must follow the confirmed copy.");
            Assert.Contains("\\Deleted", server.Commands[markerIndex], StringComparison.Ordinal);
            AssertNoPermanentRemovalCommands(server.Commands);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RetainedHistoryDeletionUsesOnlyDeletedMarkerWithoutCopyOrExpunge()
    {
        var root = CreateTestRoot();
        await using var server = ScriptedImapsServer.Start(ScriptedImapScenario.Accept);
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueRetainedHistoryAsync(store, "real-imap-retained-delete");
            var dispatcher = CreateDispatcher(store, server);

            var result = await dispatcher.DispatchNextAsync("account-a");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.WaitForCompletionAsync(timeout.Token);

            Assert.Equal(OperationEventState.Acknowledged, result!.State);
            Assert.Equal(OperationEventState.Acknowledged,
                await store.GetLatestOperationStateAsync(queued.OperationId));
            Assert.Contains(server.Commands, command =>
                command.Contains(" UID STORE 31 +FLAGS.SILENT", StringComparison.OrdinalIgnoreCase) &&
                command.Contains("\\Deleted", StringComparison.Ordinal));
            Assert.DoesNotContain(server.Commands, command =>
                command.Contains(" UID COPY ", StringComparison.OrdinalIgnoreCase));
            AssertNoPermanentRemovalCommands(server.Commands);
            Assert.Equal(1, await store.GetMessageCountAsync());
            Assert.Empty((await store.ListStoredFolderMessagesPageAsync("DeletedItems", 10)).Messages);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisconnectAfterUidStoreIsConflictAndCannotBeAutomaticallyReplayed()
    {
        var root = CreateTestRoot();
        await using var server = ScriptedImapsServer.Start(ScriptedImapScenario.DisconnectAfterUidStore);
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueFlagAsync(store, "real-imap-disconnect");
            var dispatcher = CreateDispatcher(store, server);

            var result = await dispatcher.DispatchNextAsync("account-a");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.WaitForCompletionAsync(timeout.Token);
            Assert.Null(await dispatcher.DispatchNextAsync("account-a"));

            Assert.Equal(OperationEventState.Conflict, result!.State);
            Assert.Equal("MutationOutcomeUnknown", result.ErrorCode);
            Assert.Equal(OperationEventState.Conflict,
                await store.GetLatestOperationStateAsync(queued.OperationId));
            Assert.Equal(1, server.ConnectionCount);
            Assert.Single(server.Commands, command =>
                command.Contains(" UID STORE 31 ", StringComparison.OrdinalIgnoreCase));
            AssertNoPermanentRemovalCommands(server.Commands);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MailboxMutationDispatcher CreateDispatcher(
        ModernMailStore store,
        ScriptedImapsServer server)
    {
        var sink = new MailKitImapMutationSink(
            new SecureMailEndpoint("localhost", server.Port, TlsConnectionMode.ImplicitTls),
            new MailCredentials("local-user", "local-password"),
            new EndpointPolicy(),
            new CertificateTrustPolicy([server.Certificate]));
        return new MailboxMutationDispatcher(store, sink);
    }

    private static async Task<StoredMailboxMutationOperation> QueueFlagAsync(
        ModernMailStore store,
        string key)
    {
        var message = await StoreRemoteMessageAsync(store, key);
        return await store.QueueMailboxMutationAsync(
            key,
            new OfflineMailboxMutationIntent(
                "account-a", message.MessageId, "INBOX", MailboxMutationKind.SetFlag,
                Flag: "\\Flagged"));
    }

    private static async Task<StoredMailboxMutationOperation> QueueMoveAsync(
        ModernMailStore store,
        string key)
    {
        var message = await StoreRemoteMessageAsync(store, key);
        await store.EnsureLocalFolderAsync(
            "account-a", "Archive", "Archive", null, isDeletedItems: false);
        return await store.QueueMailboxMutationAsync(
            key,
            new OfflineMailboxMutationIntent(
                "account-a", message.MessageId, "INBOX", MailboxMutationKind.MoveToFolder,
                TargetFolderId: "Archive"));
    }

    private static async Task<StoredMailboxMutationOperation> QueueRetainedHistoryAsync(
        ModernMailStore store,
        string key)
    {
        await store.EnsureLocalFolderAsync(
            "account-a", "DeletedItems", "Deleted Items", null, isDeletedItems: true);
        var message = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
            "account-a", "DeletedItems", "31", "17", null, $"<{key}@example.test>",
            key, "sender@example.test", "receiver@example.test", null,
            Encoding.UTF8.GetBytes($"Subject: {key}\r\n\r\nbody\r\n"), "body", null, []));
        await store.SaveSyncCursorAsync(new StoredSyncCursor(
            "account-a", "DeletedItems", "17", 31, DateTimeOffset.UtcNow));
        return await store.QueueMailboxMutationAsync(
            key,
            new OfflineMailboxMutationIntent(
                "account-a", message.MessageId, "DeletedItems",
                MailboxMutationKind.RetainDeletedHistory));
    }

    private static async Task<StoredMessageResult> StoreRemoteMessageAsync(
        ModernMailStore store,
        string key)
    {
        var message = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
            "account-a", "INBOX", "31", "17", null, $"<{key}@example.test>",
            key, "sender@example.test", "receiver@example.test", null,
            Encoding.UTF8.GetBytes($"Subject: {key}\r\n\r\nbody\r\n"), "body", null, []));
        await store.SaveSyncCursorAsync(new StoredSyncCursor(
            "account-a", "INBOX", "17", 31, DateTimeOffset.UtcNow));
        return message;
    }

    private static void AssertNoPermanentRemovalCommands(IReadOnlyList<string> commands)
    {
        Assert.DoesNotContain(commands, command =>
            command.Contains(" EXPUNGE", StringComparison.OrdinalIgnoreCase) ||
            command.Contains(" CLOSE", StringComparison.OrdinalIgnoreCase) ||
            command.Contains(" UID MOVE ", StringComparison.OrdinalIgnoreCase) ||
            command.Contains(" MOVE ", StringComparison.OrdinalIgnoreCase));
    }

    private static int FindCommand(IReadOnlyList<string> commands, string fragment)
    {
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index].Contains(fragment, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }

    private static async Task<ModernMailStore> CreateStoreAsync(string root)
    {
        var store = new ModernMailStore(Path.Combine(root, "mail.db"), root);
        await store.InitializeAsync();
        return store;
    }

    private static string CreateTestRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Modern")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException();
        var root = Path.Combine(
            directory.FullName, ".artifacts", "imap-mutation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
