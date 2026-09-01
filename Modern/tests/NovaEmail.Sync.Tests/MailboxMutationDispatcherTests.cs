using System.Text;
using NovaEmail.Storage;

namespace NovaEmail.Sync.Tests;

public sealed class MailboxMutationDispatcherTests
{
    [Fact]
    public async Task AcknowledgedMutationIsAccountScopedAndNeverDispatchedTwice()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueFlagAsync(store, "account-a", "ack-flag", includeRemoteIdentity: true);
            var remote = new FakeRemote();
            var dispatcher = new MailboxMutationDispatcher(store, remote);

            Assert.Null(await dispatcher.DispatchNextAsync("account-b"));
            var result = await dispatcher.DispatchNextAsync("account-a");
            Assert.Equal(OperationEventState.Acknowledged, result!.State);
            Assert.Equal(queued.OperationId, result.OperationId);
            Assert.Equal(1, remote.ApplyCount);
            Assert.Null(await dispatcher.DispatchNextAsync("account-a"));
            Assert.Equal(1, remote.ApplyCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownRemoteOutcomeBecomesVisibleConflictAndIsNeverRetried()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueFlagAsync(store, "account-a", "unknown-flag", includeRemoteIdentity: true);
            var remote = new FakeRemote(_ => throw new RemoteMailboxMutationException(
                RemoteMailboxMutationDisposition.Conflict,
                "MutationOutcomeUnknown",
                "disconnect after command"));
            var dispatcher = new MailboxMutationDispatcher(store, remote);

            var result = await dispatcher.DispatchNextAsync("account-a");

            Assert.Equal(OperationEventState.Conflict, result!.State);
            Assert.Equal("MutationOutcomeUnknown", result.ErrorCode);
            Assert.Null(await dispatcher.DispatchNextAsync("account-a"));
            Assert.Equal(1, remote.ApplyCount);
            Assert.Equal(OperationEventState.Conflict,
                await store.GetLatestOperationStateAsync(queued.OperationId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RemoteMutationTextNeverEntersResultsOrJournal()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            await QueueFlagAsync(store, "account-a", "redacted-remote-ack", includeRemoteIdentity: true);
            var reflectedSecret = "reflected-secret-" + Guid.NewGuid().ToString("N");
            var remote = new FakeRemote(_ =>
                new RemoteMailboxMutationAcknowledgement(reflectedSecret));
            var dispatcher = new MailboxMutationDispatcher(store, remote);

            var result = await dispatcher.DispatchNextAsync("account-a");
            var operation = Assert.Single(await store.ReadMailboxMutationOperationsAsync());

            Assert.Equal(OperationEventState.Acknowledged, result!.State);
            Assert.DoesNotContain(reflectedSecret, result.Detail!, StringComparison.Ordinal);
            Assert.NotNull(operation.ServerAcknowledgement);
            Assert.DoesNotContain(
                reflectedSecret, operation.ServerAcknowledgement!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RemoteMutationExceptionTextNeverEntersResult()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            await QueueFlagAsync(store, "account-a", "redacted-remote-error", includeRemoteIdentity: true);
            var reflectedSecret = "reflected-error-" + Guid.NewGuid().ToString("N");
            var remote = new FakeRemote(_ => throw new RemoteMailboxMutationException(
                RemoteMailboxMutationDisposition.Conflict,
                "MutationOutcomeUnknown",
                reflectedSecret));
            var dispatcher = new MailboxMutationDispatcher(store, remote);

            var result = await dispatcher.DispatchNextAsync("account-a");

            Assert.Equal(OperationEventState.Conflict, result!.State);
            Assert.DoesNotContain(reflectedSecret, result.Detail!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptedAttemptIsQuarantinedWithoutRemoteReplay()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueFlagAsync(store, "account-a", "interrupted-flag", includeRemoteIdentity: true);
            await store.AppendOperationEventAsync(queued.OperationId, OperationEventState.Attempting);
            var remote = new FakeRemote();
            var dispatcher = new MailboxMutationDispatcher(store, remote);

            Assert.Null(await dispatcher.DispatchNextAsync("account-a"));
            Assert.Equal(0, remote.ApplyCount);
            var operation = Assert.Single(await store.ReadMailboxMutationOperationsAsync());
            Assert.Equal(OperationEventState.Conflict, operation.State);
            Assert.Equal("InterruptedMailboxMutationUnknown", operation.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingRemoteIdentityConflictsBeforeAnyNetworkCall()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            await QueueFlagAsync(store, "account-a", "local-only-flag", includeRemoteIdentity: false);
            var remote = new FakeRemote();
            var dispatcher = new MailboxMutationDispatcher(store, remote);

            var result = await dispatcher.DispatchNextAsync("account-a");

            Assert.Equal(OperationEventState.Conflict, result!.State);
            Assert.Equal("MissingRemoteIdentity", result.ErrorCode);
            Assert.Equal(0, remote.ApplyCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DefinitelyNotStartedMutationCanBeRetriedByAnotherExplicitRequest()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueFlagAsync(store, "account-a", "retry-flag", includeRemoteIdentity: true);
            var remote = new FakeRemote(operation =>
            {
                if (operation.WasAlreadyPresent) throw new InvalidOperationException();
                return new RemoteMailboxMutationAcknowledgement("imap-store:17:31:SetFlag:\\Flagged");
            })
            {
                RetryFirst = true,
            };
            var dispatcher = new MailboxMutationDispatcher(store, remote);

            var first = await dispatcher.DispatchNextAsync("account-a");
            var second = await dispatcher.DispatchNextAsync("account-a");

            Assert.Equal(OperationEventState.RetryScheduled, first!.State);
            Assert.Equal("MutationNotStarted", first.ErrorCode);
            Assert.Equal(OperationEventState.Acknowledged, second!.State);
            Assert.Equal(2, remote.ApplyCount);
            Assert.Null(await dispatcher.DispatchNextAsync("account-a"));
            Assert.Equal(OperationEventState.Acknowledged,
                await store.GetLatestOperationStateAsync(queued.OperationId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<StoredMailboxMutationOperation> QueueFlagAsync(
        ModernMailStore store,
        string accountId,
        string key,
        bool includeRemoteIdentity)
    {
        var uid = includeRemoteIdentity ? "31" : null;
        var validity = includeRemoteIdentity ? "17" : null;
        var message = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
            accountId, "INBOX", uid, validity, null, $"<{key}@example.test>",
            key, "sender@example.test", "receiver@example.test", null,
            Encoding.UTF8.GetBytes($"Subject: {key}\r\n\r\nbody\r\n"), "body", null, []));
        if (includeRemoteIdentity)
            await store.SaveSyncCursorAsync(new StoredSyncCursor(
                accountId, "INBOX", "17", 31, DateTimeOffset.UtcNow));
        return await store.QueueMailboxMutationAsync(
            key,
            new OfflineMailboxMutationIntent(
                accountId, message.MessageId, "INBOX", MailboxMutationKind.SetFlag,
                Flag: "\\Flagged"));
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
            directory.FullName, ".artifacts", "mailbox-mutation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FakeRemote(
        Func<StoredMailboxMutationOperation, RemoteMailboxMutationAcknowledgement>? apply = null)
        : IRemoteImapMutationSink
    {
        public int ApplyCount { get; private set; }
        public bool RetryFirst { get; init; }

        public Task<RemoteMailboxMutationAcknowledgement> ApplyAsync(
            StoredMailboxMutationOperation operation,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            if (RetryFirst && ApplyCount == 1)
                throw new RemoteMailboxMutationException(
                    RemoteMailboxMutationDisposition.RetryScheduled,
                    "MutationNotStarted",
                    "connection refused before command");
            return Task.FromResult(apply?.Invoke(operation) ??
                new RemoteMailboxMutationAcknowledgement("imap-acknowledged"));
        }
    }
}
