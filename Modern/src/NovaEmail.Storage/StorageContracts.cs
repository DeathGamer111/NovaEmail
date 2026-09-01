namespace NovaEmail.Storage;

public sealed record MessageAttachmentImport(
    int Ordinal,
    string FileName,
    string MediaType,
    byte[] Content,
    string ContentId = "");

public sealed record ReceivedMessageImport(
    string AccountId,
    string FolderId,
    string? ImapUid,
    string? ImapUidValidity,
    string? PopUidl,
    string? RfcMessageId,
    string Subject,
    string From,
    string To,
    DateTimeOffset? SentUtc,
    byte[] RawMime,
    string? TextBody,
    string? SanitizableHtmlBody,
    IReadOnlyCollection<string> Flags,
    IReadOnlyList<MessageAttachmentImport>? Attachments = null);

public sealed record StoredAttachmentSummary(
    string MessageId,
    int Ordinal,
    string FileName,
    string MediaType,
    long ByteCount,
    string ContentSha256,
    string ContentId);

public sealed record StoredSyncCursor(
    string AccountId,
    string FolderId,
    string UidValidity,
    ulong HighestUid,
    DateTimeOffset UpdatedUtc);

public sealed record StoredMessageResult(
    string MessageId,
    string IdentityKey,
    string RawMimeSha256,
    long RawMimeBytes,
    bool WasAlreadyPresent);

public sealed record SentMessageProjectionInput(
    string RfcMessageId,
    string Subject,
    string From,
    string To,
    DateTimeOffset? SentUtc,
    string? TextBody,
    string? SanitizableHtmlBody,
    IReadOnlyList<MessageAttachmentImport>? Attachments = null);

public sealed record StoredSentMessageProjection(
    string OperationId,
    string AccountId,
    string FolderId,
    string MessageId,
    string RawMimeSha256,
    long RawMimeBytes,
    bool WasAlreadyPresent);

public sealed record StoredSentItemsProjectionEvidence(
    string OperationId,
    string AccountId,
    string FolderId,
    string MessageId,
    string RawMimeSha256,
    long RawMimeBytes,
    OperationEventState OperationState,
    bool AccountBound,
    bool FolderIsSentItems,
    bool FolderLinkActive,
    bool MessageActive,
    bool OutboundMimeMatchesMessage);

public sealed record StoredMessageSearchResult(
    string MessageId,
    string FolderId,
    string Subject,
    string Sender,
    string Recipients,
    DateTimeOffset? SentUtc,
    string Snippet,
    string RawMimeSha256,
    long RawMimeBytes,
    IReadOnlyList<string> Flags);

public sealed record StoredMessagePage(
    IReadOnlyList<StoredMessageSearchResult> Messages,
    long? NextOffset);

public sealed record StoredFolderSummary(
    string AccountId,
    string FolderId,
    string? ParentFolderId,
    string DisplayName,
    long ActiveMessageCount,
    long TombstonedMessageCount,
    bool IsSentItems,
    bool IsDeletedItems,
    bool IsTombstoned,
    bool IsLocalOnly,
    string? RemoteFullName,
    bool IsUserManaged = false);

public sealed record StoredLocalFolderRevision(
    string FolderId,
    int Revision,
    string DisplayName,
    bool IsTombstoned,
    DateTimeOffset SavedUtc);

public sealed record StoredLocalFolderHierarchyRevision(
    long Sequence,
    string FolderId,
    string? ParentFolderId,
    DateTimeOffset SavedUtc);

public enum CalendarEventSourceKind
{
    LocalManual,
    EmailSuggestion,
    ImportedIcs,
    Google,
    Apple,
}

public sealed record LocalCalendarEventRevisionInput(
    string? EventId,
    string Title,
    DateTime StartLocal,
    DateTime EndLocal,
    bool AllDay,
    string Location,
    string Notes,
    CalendarEventSourceKind SourceKind,
    string? EvidenceId = null,
    string? SourceMessageIdentity = null,
    string? ProviderAccountId = null,
    string? ProviderEventId = null,
    string? ProviderSeriesId = null);

public sealed record StoredLocalCalendarEventRevision(
    string EventId,
    int Revision,
    string Title,
    DateTime StartLocal,
    DateTime EndLocal,
    bool AllDay,
    string Location,
    string Notes,
    bool IsTombstoned,
    CalendarEventSourceKind SourceKind,
    string? EvidenceId,
    string? SourceMessageIdentitySha256,
    string? ProviderAccountId,
    string? ProviderEventId,
    DateTimeOffset SavedUtc,
    string? ProviderSeriesId = null);

public sealed record LocalCalendarEventSaveResult(
    StoredLocalCalendarEventRevision Event,
    bool WasAlreadyPresent);

public enum CalendarProviderObservationOutcome
{
    Added,
    Updated,
    Unchanged,
    Tombstoned,
    AlreadyTombstoned,
    MissingDeletionRetained,
    ResurrectionConflictRetained,
}

public sealed record CalendarProviderEventSnapshotImport(
    CalendarEventSourceKind SourceKind,
    string ProviderAccountIdentitySha256,
    string ProviderEventIdentitySha256,
    string ProviderRevisionIdentitySha256,
    string Title,
    DateTime StartLocal,
    DateTime EndLocal,
    bool AllDay,
    string Location,
    string Notes,
    bool IsDeleted,
    DateTimeOffset ObservedUtc,
    string? ProviderSeriesIdentitySha256 = null);

public sealed record CalendarProviderSeriesSnapshotImport(
    CalendarEventSourceKind SourceKind,
    string ProviderAccountIdentitySha256,
    string ProviderSeriesIdentitySha256,
    string ProviderSeriesRevisionIdentitySha256,
    IReadOnlyList<string> MemberEventIdentitySha256s,
    DateTimeOffset ObservedUtc);

public sealed record StoredCalendarProviderSeriesSnapshot(
    CalendarEventSourceKind SourceKind,
    string ProviderAccountIdentitySha256,
    string ProviderSeriesIdentitySha256,
    string ProviderSeriesRevisionIdentitySha256,
    string PayloadSha256,
    IReadOnlyList<string> MemberEventIdentitySha256s,
    DateTimeOffset ObservedUtc);

public sealed record CalendarProviderSeriesSaveResult(
    StoredCalendarProviderSeriesSnapshot Snapshot,
    bool WasAlreadyPresent);

public sealed record StoredCalendarProviderObservation(
    CalendarEventSourceKind SourceKind,
    string ProviderAccountIdentitySha256,
    string ProviderEventIdentitySha256,
    string ProviderRevisionIdentitySha256,
    string PayloadSha256,
    string? EventId,
    CalendarProviderObservationOutcome Outcome,
    DateTimeOffset ObservedUtc,
    string? ProviderSeriesIdentitySha256 = null);

public sealed record CalendarProviderEventApplyResult(
    StoredCalendarProviderObservation Observation,
    StoredLocalCalendarEventRevision? Event,
    bool WasAlreadyObserved);

public sealed record OperationRegistration(
    string OperationId,
    string IdempotencyKey,
    string OperationType,
    string PayloadSha256,
    bool WasAlreadyPresent);

public sealed record MailAccountProfileInput(
    string AccountId,
    string DisplayName,
    string ReceiveProtocol,
    string ReceiveHost,
    int ReceivePort,
    string ReceiveTlsMode,
    string SmtpHost,
    int SmtpPort,
    string SmtpTlsMode,
    string CredentialUserName,
    string SenderAddress);

public sealed record StoredMailAccountProfile(
    string AccountId,
    long Revision,
    string DisplayName,
    string ReceiveProtocol,
    string ReceiveHost,
    int ReceivePort,
    string ReceiveTlsMode,
    string SmtpHost,
    int SmtpPort,
    string SmtpTlsMode,
    string CredentialUserName,
    string SenderAddress,
    DateTimeOffset SavedUtc);

public enum ContactGroupMemberKind
{
    EmailAddress,
    FriendlyName,
    NestedGroup,
}

public sealed record ContactGroupMemberInput(
    ContactGroupMemberKind Kind,
    string Value);

public sealed record ContactRevisionInput(
    string? ContactId,
    string DisplayName,
    string EmailAddress,
    string Notes,
    bool IsGroup,
    IReadOnlyList<ContactGroupMemberInput>? GroupMembers = null);

public sealed record StoredContactGroupMember(
    int Ordinal,
    ContactGroupMemberKind Kind,
    string Value);

public sealed record StoredContactRevision(
    string ContactId,
    int Revision,
    string DisplayName,
    string EmailAddress,
    string Notes,
    bool IsGroup,
    bool IsTombstoned,
    DateTimeOffset SavedUtc,
    IReadOnlyList<StoredContactGroupMember> GroupMembers);

public static class MailAccountProfilePolicy
{
    public const string LocalOutboxStagingAccountId = "local-outbox-staging";
}

public sealed record QueuedOutboundMessage(
    string OperationId,
    string IdempotencyKey,
    string AccountId,
    string MessageId,
    string RawMimeSha256,
    long RawMimeBytes,
    string EnvelopeSender,
    IReadOnlyList<string> EnvelopeRecipients);

public sealed record StoredOutboundOperationSummary(
    string OperationId,
    string IdempotencyKey,
    string? AccountId,
    string MessageId,
    string RawMimeSha256,
    long RawMimeBytes,
    OperationEventState State,
    DateTimeOffset EventUtc,
    string? ServerAcknowledgement,
    string? ErrorCode);

public enum MailboxMutationKind
{
    MoveToFolder,
    SetFlag,
    ClearFlag,
    MoveToDeletedItems,
    RetainDeletedHistory,
}

public sealed record OfflineMailboxMutationIntent(
    string AccountId,
    string MessageId,
    string SourceFolderId,
    MailboxMutationKind Kind,
    string? TargetFolderId = null,
    string? Flag = null);

public sealed record StoredMailboxMutationOperation(
    string OperationId,
    string IdempotencyKey,
    string AccountId,
    string MessageId,
    string SourceFolderId,
    MailboxMutationKind Kind,
    string? TargetFolderId,
    string? Flag,
    string? ExpectedUidValidity,
    ulong? ExpectedUid,
    OperationEventState State,
    DateTimeOffset EventUtc,
    string? ServerAcknowledgement,
    string? ErrorCode,
    bool WasAlreadyPresent);

public sealed record StoredDeletedItemsRetentionEvidence(
    string OperationId,
    string AccountId,
    string MessageId,
    string SourceFolderId,
    bool SourceIsDeletedItems,
    bool SourceLinkTombstoned,
    bool MessageTombstoned,
    string RawMimeSha256,
    long RawMimeBytes,
    OperationEventState State);

public sealed record DraftRevisionImport(
    string? DraftId,
    byte[] RawMime,
    string Subject,
    string Sender,
    string Recipients,
    string BodySnippet);

public sealed record StoredDraftRevision(
    string DraftId,
    int Revision,
    string RawMimeSha256,
    long RawMimeBytes,
    string Subject,
    string Sender,
    string Recipients,
    string BodySnippet,
    DateTimeOffset SavedUtc,
    bool IsTombstoned);

public sealed record StoredDraftRevisionEvidence(
    string DraftId,
    int Revision,
    string RawMimeSha256,
    long RawMimeBytes,
    DateTimeOffset SavedUtc,
    bool IsLatestRevision,
    bool DraftTombstoned);

public enum OperationEventState
{
    Queued,
    Attempting,
    Acknowledged,
    RetryScheduled,
    Conflict,
    Failed,
}

public sealed record ModernStoreBackupResult(
    string BackupRoot,
    string ManifestSha256,
    int FileCount,
    long TotalBytes);

public sealed record ModernStoreRestoreResult(
    string RestoredDataRoot,
    string RestoredDatabasePath,
    string ManifestSha256,
    int FileCount,
    long TotalBytes);

/// <summary>
/// Read-only accounting for the normalized store.  This is deliberately not a
/// cleanup plan: NovaEmail retains tombstones and immutable revisions, so a
/// development build never reports them as reclaimable or offers physical
/// compaction.
/// </summary>
public sealed record StoredNoPurgeCompactionAssessment(
    long DatabaseBytes,
    long ContentBlobCount,
    long ContentBlobBytes,
    long MessageCount,
    long TombstonedMessageCount,
    long FolderMessageLinkCount,
    long TombstonedFolderMessageLinkCount,
    long FolderCount,
    long TombstonedFolderCount,
    long AttachmentCount,
    long AttachmentBytes,
    long ImmutableRevisionCount,
    long PendingOperationCount,
    long ReclaimableBytes,
    bool PhysicalPurgePermitted);
