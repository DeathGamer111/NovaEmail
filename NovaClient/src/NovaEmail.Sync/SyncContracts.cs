namespace NovaEmail.Sync;

public enum RemoteMessageChangeKind
{
    Upsert,
    FlagsOnly,
    Removed,
}

public sealed record RemoteMessageChange(
    RemoteMessageChangeKind Kind,
    ulong Uid,
    byte[]? RawMime,
    IReadOnlyCollection<string> Flags);

public sealed record RemoteMailboxSnapshot(
    string UidValidity,
    ulong HighestUid,
    IReadOnlyList<RemoteMessageChange> Changes,
    bool IsCompleteSnapshot);

public interface IRemoteImapSnapshotSource
{
    Task<RemoteMailboxSnapshot> GetSnapshotAsync(
        RemoteSyncCheckpoint checkpoint,
        CancellationToken cancellationToken = default);
}

public sealed record RemoteSyncCheckpoint(
    string? UidValidity,
    ulong HighestKnownUid,
    IReadOnlySet<ulong> KnownUids);

public sealed record ImapSyncResult(
    bool UidValidityChanged,
    int Imported,
    int Reconciled,
    int Tombstoned,
    IReadOnlyList<string> Conflicts);
