using System.Security.Cryptography;
using System.Text;
using NovaEmail.Mail;
using NovaEmail.Safety;
using NovaEmail.Storage;

namespace NovaEmail.Sync;

public sealed record ImapFolderSyncResult(
    string FolderId,
    string RemoteFullName,
    string DisplayName,
    bool IsInbox,
    ImapSyncResult Result);

public sealed record ImapAccountSyncResult(
    IReadOnlyList<ImapFolderSyncResult> Folders)
{
    public int Imported => Folders.Sum(folder => folder.Result.Imported);
    public int Reconciled => Folders.Sum(folder => folder.Result.Reconciled);
    public int Tombstoned => Folders.Sum(folder => folder.Result.Tombstoned);
    public IReadOnlyList<string> Conflicts => Folders
        .SelectMany(folder => folder.Result.Conflicts.Select(
            conflict => $"{folder.DisplayName}: {conflict}"))
        .ToArray();
    public string? InboxFolderId => Folders.FirstOrDefault(folder => folder.IsInbox)?.FolderId;
}

/// <summary>
/// Discovers and synchronizes selectable IMAP folders through read-only server operations.
/// Server-side mailbox mutation and SMTP submission are deliberately outside this coordinator.
/// </summary>
public sealed class ImapAccountSyncCoordinator
{
    private const int MaximumSynchronizedFolders = 100;
    private readonly ModernMailStore _store;
    private readonly Func<CancellationToken, Task<IReadOnlyList<RemoteImapFolderCatalogEntry>>> _readCatalogAsync;
    private readonly Func<string, IRemoteImapSnapshotSource> _createSnapshotSource;

    public ImapAccountSyncCoordinator(
        ModernMailStore store,
        SecureMailEndpoint endpoint,
        MailCredentials credentials,
        EndpointPolicy endpointPolicy,
        CertificateTrustPolicy certificateTrustPolicy)
        : this(
            store,
            new MailKitImapFolderCatalog(
                endpoint, credentials, endpointPolicy, certificateTrustPolicy).ReadAsync,
            folderName => new MailKitImapSnapshotSource(
                endpoint, credentials, folderName, endpointPolicy, certificateTrustPolicy))
    {
    }

    internal ImapAccountSyncCoordinator(
        ModernMailStore store,
        Func<CancellationToken, Task<IReadOnlyList<RemoteImapFolderCatalogEntry>>> readCatalogAsync,
        Func<string, IRemoteImapSnapshotSource> createSnapshotSource)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _readCatalogAsync = readCatalogAsync ?? throw new ArgumentNullException(nameof(readCatalogAsync));
        _createSnapshotSource = createSnapshotSource ?? throw new ArgumentNullException(nameof(createSnapshotSource));
    }

    public async Task<ImapAccountSyncResult> SynchronizeAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var catalog = await _readCatalogAsync(cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("IMAP folder discovery returned no catalog contract.");
        if (catalog.Any(folder => folder is null))
            throw new InvalidDataException("IMAP folder discovery returned an incomplete catalog entry.");

        var selectable = catalog
            .Where(folder => folder.IsSelectable)
            .OrderByDescending(folder => folder.IsInbox)
            .ThenBy(folder => folder.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(folder => folder.FullName, StringComparer.Ordinal)
            .ToArray();
        if (selectable.Length > MaximumSynchronizedFolders)
            throw new InvalidDataException(
                $"This account exposes more than {MaximumSynchronizedFolders} selectable folders; " +
                "narrow folder selection before synchronizing it in this demo.");

        var engine = new ImapSyncEngine(_store);
        var results = new List<ImapFolderSyncResult>(selectable.Length);
        foreach (var folder in selectable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderId = BuildFolderId(accountId, folder.FullName);
            await _store.EnsureImapFolderAsync(
                accountId,
                folderId,
                folder.Name,
                folder.FullName,
                designateAsDeletedItems: folder.SpecialUses.Contains("Trash", StringComparer.Ordinal),
                cancellationToken).ConfigureAwait(false);
            var result = await engine.SynchronizeAsync(
                accountId,
                folderId,
                _createSnapshotSource(folder.FullName),
                cancellationToken).ConfigureAwait(false);
            results.Add(new ImapFolderSyncResult(
                folderId, folder.FullName, folder.Name, folder.IsInbox, result));
        }

        return new ImapAccountSyncResult(results);
    }

    internal static string BuildFolderId(string accountId, string remoteFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteFullName);
        var identity = Encoding.UTF8.GetBytes(accountId + "\n" + remoteFullName);
        return "imap-" + Convert.ToHexStringLower(SHA256.HashData(identity))[..32];
    }
}
