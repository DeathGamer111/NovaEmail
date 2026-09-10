using MimeKit;
using NovaEmail.Mail;
using NovaEmail.Storage;

namespace NovaEmail.Sync;

public sealed class ImapSyncEngine
{
    private const int MaximumRemoteChanges = 100_000;
    private const int MaximumMessageBytes = 100 * 1024 * 1024;
    private readonly ModernMailStore _store;

    public ImapSyncEngine(ModernMailStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ImapSyncResult> SynchronizeAsync(
        string accountId,
        string folderId,
        IRemoteImapSnapshotSource remote,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        ArgumentNullException.ThrowIfNull(remote);
        var cursor = await _store.GetSyncCursorAsync(accountId, folderId, cancellationToken).ConfigureAwait(false);
        var knownUids = cursor is null
            ? (IReadOnlySet<ulong>)new HashSet<ulong>()
            : await _store.GetRemoteUidsAsync(
                accountId, folderId, cursor.UidValidity, cancellationToken).ConfigureAwait(false);
        var snapshot = await remote.GetSnapshotAsync(
            new RemoteSyncCheckpoint(cursor?.UidValidity, cursor?.HighestUid ?? 0, knownUids),
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.Changes is null)
            throw new InvalidDataException("Remote mailbox source returned an incomplete snapshot contract.");
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.UidValidity);
        if (snapshot.Changes.Count > MaximumRemoteChanges)
            throw new InvalidDataException("Remote mailbox snapshot exceeds the configured change limit.");
        if (snapshot.Changes.Any(change => change is null || change.Flags is null))
            throw new InvalidDataException("Remote mailbox snapshot contains an incomplete change contract.");

        var uidValidityChanged = cursor is not null &&
            !string.Equals(cursor.UidValidity, snapshot.UidValidity, StringComparison.Ordinal);
        if (uidValidityChanged && !snapshot.IsCompleteSnapshot)
            throw new InvalidDataException("UIDVALIDITY changed without a complete deterministic resynchronization snapshot.");
        var snapshotGenerationUids = cursor is not null && !uidValidityChanged
            ? knownUids
            : await _store.GetRemoteUidsAsync(
                accountId, folderId, snapshot.UidValidity, cancellationToken).ConfigureAwait(false);

        var imported = 0;
        var reconciled = 0;
        var tombstoned = 0;
        var conflicts = new List<string>();
        var seenUids = new HashSet<ulong>();
        var progressUid = !uidValidityChanged && cursor is not null ? cursor.HighestUid : 0UL;
        if (uidValidityChanged)
        {
            foreach (var staleUid in knownUids.Order())
            {
                if (await _store.TombstoneRemoteMessageAsync(
                    accountId, folderId, cursor!.UidValidity, staleUid, cancellationToken).ConfigureAwait(false))
                    tombstoned++;
                else
                    conflicts.Add($"UIDVALIDITY reset could not tombstone prior UID {staleUid}.");
            }
        }
        foreach (var change in snapshot.Changes.OrderBy(change => change.Uid))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.Uid == 0 || change.Uid > snapshot.HighestUid || !seenUids.Add(change.Uid))
                throw new InvalidDataException("Remote mailbox snapshot contains an invalid or duplicate UID.");
            if (change.Kind == RemoteMessageChangeKind.Removed)
            {
                if (await _store.TombstoneRemoteMessageAsync(
                    accountId, folderId, snapshot.UidValidity, change.Uid, cancellationToken).ConfigureAwait(false))
                    tombstoned++;
                else
                    conflicts.Add($"Server removed UID {change.Uid}, but no matching local identity was found.");
                continue;
            }
            if (change.Kind == RemoteMessageChangeKind.FlagsOnly)
            {
                if (change.RawMime is not null)
                    throw new InvalidDataException($"Flags-only UID {change.Uid} unexpectedly included MIME bytes.");
                if (await _store.ReplaceRemoteMessageFlagsAsync(
                    accountId, folderId, snapshot.UidValidity, change.Uid, change.Flags,
                    cancellationToken).ConfigureAwait(false))
                    reconciled++;
                else
                    conflicts.Add($"Server reported flags for UID {change.Uid}, but no matching local identity was found.");
                continue;
            }
            if (change.Kind != RemoteMessageChangeKind.Upsert)
                throw new InvalidDataException($"Remote UID {change.Uid} has an unsupported change kind.");
            if (change.RawMime is null or { Length: 0 } || change.RawMime.Length > MaximumMessageBytes)
                throw new InvalidDataException($"Remote UID {change.Uid} has an empty or oversized MIME payload.");

            var message = await SafeMimeParser.ParseAsync(
                change.RawMime, cancellationToken).ConfigureAwait(false);
            var materializedAttachments = await SafeAttachmentExporter.MaterializeAsync(
                message, cancellationToken: cancellationToken).ConfigureAwait(false);
            var result = await _store.StoreReceivedMessageAsync(
                new ReceivedMessageImport(
                    accountId,
                    folderId,
                    change.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    snapshot.UidValidity,
                    null,
                    message.MessageId,
                    message.Subject ?? string.Empty,
                    message.From.ToString(),
                    message.To.ToString(),
                    message.Date == DateTimeOffset.MinValue ? null : message.Date.ToUniversalTime(),
                    change.RawMime,
                    message.TextBody,
                    message.HtmlBody,
                    change.Flags,
                    materializedAttachments.Select(attachment => new MessageAttachmentImport(
                        attachment.Ordinal,
                        attachment.FileName,
                        attachment.MediaType,
                        attachment.Content,
                        attachment.ContentId)).ToArray()),
                cancellationToken).ConfigureAwait(false);
            if (result.WasAlreadyPresent) reconciled++; else imported++;

            if (!uidValidityChanged && change.Uid > progressUid)
            {
                progressUid = change.Uid;
                await _store.SaveSyncCursorAsync(
                    new StoredSyncCursor(accountId, folderId, snapshot.UidValidity, progressUid, DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (snapshot.IsCompleteSnapshot)
        {
            foreach (var missingUid in snapshotGenerationUids.Where(uid => !seenUids.Contains(uid)).Order())
            {
                if (await _store.TombstoneRemoteMessageAsync(
                    accountId, folderId, snapshot.UidValidity, missingUid, cancellationToken).ConfigureAwait(false))
                    tombstoned++;
                else
                    conflicts.Add($"Complete snapshot omitted UID {missingUid}, but no matching local identity was found.");
            }
        }

        var finalHighestUid = !uidValidityChanged && cursor is not null
            ? Math.Max(cursor.HighestUid, snapshot.HighestUid)
            : snapshot.HighestUid;
        await _store.SaveSyncCursorAsync(
            new StoredSyncCursor(accountId, folderId, snapshot.UidValidity, finalHighestUid, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        return new ImapSyncResult(
            uidValidityChanged, imported, reconciled, tombstoned, conflicts);
    }
}
