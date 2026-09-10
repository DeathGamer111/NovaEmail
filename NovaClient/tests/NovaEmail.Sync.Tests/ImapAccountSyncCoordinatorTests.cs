using System.Text;
using NovaEmail.Storage;

namespace NovaEmail.Sync.Tests;

public sealed class ImapAccountSyncCoordinatorTests
{
    [Fact]
    public async Task SelectableFoldersAreBoundAndSynchronizedWithoutServerMutation()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var catalogs = new[]
            {
                new RemoteImapFolderCatalogEntry(
                    "INBOX", "Inbox", '/', true, true, true, false, []),
                new RemoteImapFolderCatalogEntry(
                    "Projects", "Projects", '/', false, false, true, true, []),
                new RemoteImapFolderCatalogEntry(
                    "Projects/Nova", "Nova", '/', true, false, true, false, []),
            };
            var sources = new Dictionary<string, RemoteMailboxSnapshot>(StringComparer.Ordinal)
            {
                ["INBOX"] = Snapshot(1, "inbox@example.test", "Inbox body"),
                ["Projects/Nova"] = Snapshot(8, "project@example.test", "Project body"),
            };
            var requestedFolders = new List<string>();
            var coordinator = new ImapAccountSyncCoordinator(
                store,
                _ => Task.FromResult<IReadOnlyList<RemoteImapFolderCatalogEntry>>(catalogs),
                folder =>
                {
                    requestedFolders.Add(folder);
                    return new FakeRemote(sources[folder]);
                });

            var result = await coordinator.SynchronizeAsync("account-one");

            Assert.Equal(2, result.Imported);
            Assert.Equal(["INBOX", "Projects/Nova"], requestedFolders);
            Assert.NotNull(result.InboxFolderId);
            Assert.Empty(result.Conflicts);
            var folders = await store.ReadStoredFolderSummariesAsync();
            Assert.Equal(2, folders.Count);
            Assert.Contains(folders, folder =>
                folder.FolderId == result.InboxFolderId && folder.RemoteFullName == "INBOX");
            Assert.All(folders, folder => Assert.False(folder.IsLocalOnly));
            Assert.Single((await store.ListStoredFolderMessagesPageAsync(
                result.InboxFolderId!, 10)).Messages);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void FolderIdentityIsStableAccountScopedAndDoesNotExposeRemoteName()
    {
        var first = ImapAccountSyncCoordinator.BuildFolderId("account-one", "Private/2026");
        var replay = ImapAccountSyncCoordinator.BuildFolderId("account-one", "Private/2026");
        var anotherAccount = ImapAccountSyncCoordinator.BuildFolderId("account-two", "Private/2026");

        Assert.Equal(first, replay);
        Assert.NotEqual(first, anotherAccount);
        Assert.DoesNotContain("Private", first, StringComparison.Ordinal);
        Assert.StartsWith("imap-", first, StringComparison.Ordinal);
    }

    private static RemoteMailboxSnapshot Snapshot(ulong uid, string messageId, string body) =>
        new("100", uid,
            [new RemoteMessageChange(
                RemoteMessageChangeKind.Upsert,
                uid,
                Encoding.ASCII.GetBytes(
                    $"From: sender@example.test\r\nTo: receiver@example.test\r\n" +
                    $"Subject: Sync test\r\nMessage-ID: <{messageId}>\r\n" +
                    $"Date: Thu, 13 Aug 2026 12:00:00 -0500\r\n\r\n{body}\r\n"),
                [])],
            IsCompleteSnapshot: true);

    private static async Task<ModernMailStore> CreateStoreAsync(string testRoot)
    {
        var data = Path.Combine(testRoot, "data");
        var store = new ModernMailStore(Path.Combine(data, "mail.db"), data);
        await store.InitializeAsync();
        return store;
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaEmail", "sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FakeRemote(RemoteMailboxSnapshot snapshot) : IRemoteImapSnapshotSource
    {
        public Task<RemoteMailboxSnapshot> GetSnapshotAsync(
            RemoteSyncCheckpoint checkpoint,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }
}
