using System.Collections.Immutable;

namespace NovaEmail.Domain;

public sealed record AccountId(Guid Value);
public sealed record FolderId(Guid Value);
public sealed record MessageId(Guid Value);
public sealed record AttachmentId(Guid Value);

public enum MailProtocol
{
    Pop3,
    Imap,
}

public enum PendingOperationState
{
    Queued,
    Running,
    Acknowledged,
    RetryableFailure,
    Conflict,
    PermanentlyFailed,
}

public sealed record Account(
    AccountId Id,
    string DisplayName,
    MailProtocol ReceiveProtocol,
    string ReceiveHost,
    int ReceivePort,
    string SmtpHost,
    int SmtpPort,
    bool RequireValidatedTls);

public sealed record Folder(
    FolderId Id,
    AccountId AccountId,
    string DisplayName,
    string? ServerPath,
    bool IsDeletedItems);

public sealed record MessageIdentity(
    AccountId AccountId,
    FolderId FolderId,
    string? ImapUidValidity,
    uint? ImapUid,
    string? PopUidl,
    string? RfcMessageId,
    string ContentSha256);

public sealed record Message(
    MessageId Id,
    MessageIdentity Identity,
    string Subject,
    string From,
    ImmutableArray<string> To,
    DateTimeOffset ReceivedAt,
    bool IsRead,
    bool IsDeleted);

public sealed record Attachment(
    AttachmentId Id,
    MessageId MessageId,
    string FileName,
    string MediaType,
    long Length,
    string ContentSha256);

public sealed record SyncCursor(
    AccountId AccountId,
    FolderId FolderId,
    string? ImapUidValidity,
    uint? HighestImapUid,
    string? HighestModSequence,
    ImmutableHashSet<string> PopUidls);

public sealed record PendingOperation(
    Guid Id,
    string IdempotencyKey,
    string OperationType,
    DateTimeOffset CreatedAt,
    PendingOperationState State,
    int AttemptCount,
    string PayloadJson,
    string? ServerAcknowledgement,
    string? LastError);
