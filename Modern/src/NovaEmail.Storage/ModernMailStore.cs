using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using NovaEmail.Safety;

namespace NovaEmail.Storage;

public sealed class ModernMailStore
{
    private const int MaximumRemoteFlags = 128;
    private const int MaximumRemoteFlagCharacters = 255;
    private const int MaximumAttachmentsPerMessage = 2_048;
    private const long MaximumDecodedAttachmentBytes = 100L * 1024L * 1024L;
    private const long MaximumAggregateAttachmentBytes = 100L * 1024L * 1024L;
    internal const long MaximumRawMimeBytes = 100L * 1024L * 1024L;
    private const char EnvelopeRecipientSeparator = '\u001f';
    private const string CalendarLocalTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
    private readonly string _connectionString;
    private readonly ContentAddressedStore _content;
    internal string DatabasePath { get; }
    internal string DataRoot { get; }
    public string ApprovedDataRoot => DataRoot;

    internal ModernMailStore(string databasePath, string approvedDataRoot)
        : this(databasePath, approvedDataRoot, beforeContentWrite: null)
    {
    }

    public static ModernMailStore CreateLocalStore()
    {
        var dataRoot = Path.Combine(ApplicationIdentity.LocalDataRoot, "Data");
        return new ModernMailStore(Path.Combine(dataRoot, "mail.db"), dataRoot);
    }

    public static ModernMailStore CreateFixtureRunStore(
        ApprovedFixtureStoreRoot approvedRoot)
    {
        ArgumentNullException.ThrowIfNull(approvedRoot);
        var dataRoot = approvedRoot.ApprovedDataRoot;
        if (!Directory.Exists(dataRoot))
            throw new DirectoryNotFoundException(
                "The fixed disposable test-store child must be pre-created with a protected current-user ACL.");
        ReparsePointPolicy.EnsureNoTraversal(dataRoot, requireLeafExists: true);
        var access = new CurrentUserOnlyAclProbe().Inspect(dataRoot);
        if (!access.Passed)
            throw new NovaEmail.Safety.SecurityException(
                $"The disposable test-store ACL gate did not pass: {access.Detail}");
        return new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
    }

    internal ModernMailStore(
        string databasePath,
        string approvedDataRoot,
        Action<long>? beforeContentWrite)
    {
        var canonicalDatabasePath = StorageRootPolicy.EnsureSafeDatabasePath(databasePath, approvedDataRoot);
        var canonicalDataRoot = Path.GetFullPath(approvedDataRoot);
        DatabasePath = canonicalDatabasePath;
        DataRoot = canonicalDataRoot;
        Directory.CreateDirectory(canonicalDataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(canonicalDatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = canonicalDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        _content = new ContentAddressedStore(canonicalDataRoot, beforeContentWrite);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "folders",
            "is_deleted_items",
            "ALTER TABLE folders ADD COLUMN is_deleted_items INTEGER NOT NULL DEFAULT 0 CHECK(is_deleted_items IN (0,1));",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "folders",
            "is_sent_items",
            "ALTER TABLE folders ADD COLUMN is_sent_items INTEGER NOT NULL DEFAULT 0 CHECK(is_sent_items IN (0,1));",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "folders",
            "remote_full_name",
            "ALTER TABLE folders ADD COLUMN remote_full_name TEXT NULL;",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "folders",
            "is_local_only",
            "ALTER TABLE folders ADD COLUMN is_local_only INTEGER NOT NULL DEFAULT 1 CHECK(is_local_only IN (0,1));",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "attachments",
            "content_id",
            "ALTER TABLE attachments ADD COLUMN content_id TEXT NOT NULL DEFAULT '';",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "outbound_messages",
            "account_id",
            "ALTER TABLE outbound_messages ADD COLUMN account_id TEXT NULL REFERENCES accounts(account_id);",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "outbound_messages",
            "envelope_sender",
            "ALTER TABLE outbound_messages ADD COLUMN envelope_sender TEXT NOT NULL DEFAULT '';",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "outbound_messages",
            "envelope_recipients",
            "ALTER TABLE outbound_messages ADD COLUMN envelope_recipients TEXT NOT NULL DEFAULT '';",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "account_profile_revisions",
            "sender_address",
            "ALTER TABLE account_profile_revisions ADD COLUMN sender_address TEXT NOT NULL DEFAULT '';",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "calendar_event_sources",
            "provider_series_id",
            "ALTER TABLE calendar_event_sources ADD COLUMN provider_series_id TEXT NULL CHECK(provider_series_id IS NULL OR (length(provider_series_id) = 64 AND provider_series_id NOT GLOB '*[^0-9a-f]*'));",
            cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(
            connection,
            "calendar_provider_observations",
            "provider_series_identity_sha256",
            "ALTER TABLE calendar_provider_observations ADD COLUMN provider_series_identity_sha256 TEXT NULL CHECK(provider_series_identity_sha256 IS NULL OR (length(provider_series_identity_sha256) = 64 AND provider_series_identity_sha256 NOT GLOB '*[^0-9a-f]*'));",
            cancellationToken).ConfigureAwait(false);
        await EnsureMailboxMutationSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        command.CommandText =
            """
            CREATE UNIQUE INDEX IF NOT EXISTS one_active_deleted_items_folder_per_account
            ON folders(account_id) WHERE is_deleted_items=1 AND is_tombstoned=0;
            CREATE UNIQUE INDEX IF NOT EXISTS one_active_sent_items_folder_per_account
            ON folders(account_id) WHERE is_sent_items=1 AND is_tombstoned=0;
            CREATE UNIQUE INDEX IF NOT EXISTS one_active_remote_folder_binding_per_account
            ON folders(account_id, remote_full_name)
            WHERE is_local_only=0 AND is_tombstoned=0;
            INSERT OR IGNORE INTO local_folder_revisions(
                folder_id, revision, display_name, is_tombstoned, saved_utc)
            SELECT folder_id, 1, display_name, is_tombstoned, $savedUtc
            FROM folders
            WHERE is_local_only=1 AND remote_full_name IS NULL
              AND folder_id LIKE 'local-%';
            INSERT INTO local_folder_hierarchy_revisions(folder_id, parent_folder_id, saved_utc)
            SELECT f.folder_id, f.parent_folder_id, $savedUtc
            FROM folders f
            WHERE EXISTS (
                SELECT 1 FROM local_folder_revisions r WHERE r.folder_id=f.folder_id)
              AND NOT EXISTS (
                SELECT 1 FROM local_folder_hierarchy_revisions h WHERE h.folder_id=f.folder_id);
            """;
        command.Parameters.AddWithValue(
            "$savedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredMessageResult> StoreReceivedMessageAsync(
        ReceivedMessageImport import,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(import);
        ArgumentException.ThrowIfNullOrWhiteSpace(import.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(import.FolderId);
        ValidateRawMimeLength(import.RawMime.Length, "Received MIME");
        var normalizedFlags = NormalizeRemoteFlags(import.Flags);
        var stored = await _content.ImportBytesAsync(import.RawMime, "mime", cancellationToken).ConfigureAwait(false);
        var identityKey = BuildIdentityKey(
            import.AccountId, import.FolderId, import.ImapUidValidity, import.ImapUid,
            import.PopUidl, import.RfcMessageId, stored.Sha256);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAccountAndFolderAsync(
            connection, transaction, import.AccountId, import.FolderId, cancellationToken).ConfigureAwait(false);
        var existing = await FindByIdentityAsync(
            connection, transaction, import.AccountId, identityKey, cancellationToken).ConfigureAwait(false);
        existing ??= await FindByPopIdentityAsync(
            connection, transaction, import.AccountId, import.PopUidl, cancellationToken).ConfigureAwait(false);
        existing ??= await FindByContentAsync(
            connection, transaction, import.AccountId, import.FolderId,
            import.RfcMessageId, stored.Sha256, cancellationToken).ConfigureAwait(false);
        var messageId = existing?.MessageId ?? Guid.NewGuid().ToString("N");
        if (existing is not null && !string.Equals(existing.Value.RawMimeSha256, stored.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("An existing remote message identity points to different MIME bytes.");

        await EnsureContentBlobAsync(
            connection, transaction, stored, "received-rfc-mime", cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO messages(
                    message_id, account_id, identity_key, imap_uid, imap_uid_validity, pop_uidl,
                    rfc_message_id, subject, raw_mime_sha256, raw_mime_bytes, imported_utc)
                VALUES (
                    $messageId, $accountId, $identityKey, $imapUid, $imapUidValidity, $popUidl,
                    $rfcMessageId, $subject, $sha256, $byteCount, $importedUtc);
                """,
                cancellationToken,
                ("$messageId", messageId), ("$accountId", import.AccountId), ("$identityKey", identityKey),
                ("$imapUid", import.ImapUid), ("$imapUidValidity", import.ImapUidValidity),
                ("$popUidl", import.PopUidl), ("$rfcMessageId", import.RfcMessageId),
                ("$subject", import.Subject), ("$sha256", stored.Sha256), ("$byteCount", stored.Bytes),
                ("$importedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)))
                .ConfigureAwait(false);
        }

        await StoreNormalizedBodyAsync(
            connection, transaction, messageId,
            import.From, import.To, import.SentUtc, import.TextBody,
            import.SanitizableHtmlBody, cancellationToken).ConfigureAwait(false);
        await IndexSearchAsync(
            connection, transaction, messageId, import.Subject,
            import.From, import.To, import.TextBody, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO folder_messages(folder_id, message_id) VALUES ($folderId, $messageId)
            ON CONFLICT(folder_id, message_id) DO UPDATE SET is_tombstoned=0;
            """,
            cancellationToken, ("$folderId", import.FolderId), ("$messageId", messageId)).ConfigureAwait(false);
        await RecordRemoteIdentityAsync(
            connection, transaction, import.AccountId, import.FolderId,
            import.ImapUidValidity, import.ImapUid, messageId, cancellationToken).ConfigureAwait(false);
        await ReplaceFolderMessageFlagsAsync(
            connection, transaction, import.FolderId, messageId, normalizedFlags, cancellationToken)
            .ConfigureAwait(false);
        await StoreMessageAttachmentsAsync(
            connection, transaction, messageId, import.Attachments,
            "received-attachment", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoredMessageResult(messageId, identityKey, stored.Sha256, stored.Bytes, existing is not null);
    }

    public async Task<bool> ReplaceRemoteMessageFlagsAsync(
        string accountId,
        string folderId,
        string uidValidity,
        ulong uid,
        IReadOnlyCollection<string> flags,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(uidValidity);
        var normalizedFlags = NormalizeRemoteFlags(flags);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText =
            """
            SELECT ri.message_id
            FROM message_remote_identities ri
            JOIN folder_messages fm
              ON fm.folder_id = ri.folder_id AND fm.message_id = ri.message_id
            WHERE ri.account_id = $accountId AND ri.folder_id = $folderId
              AND ri.uid_validity = $uidValidity AND ri.uid = $uid
              AND fm.is_tombstoned = 0;
            """;
        lookup.Parameters.AddWithValue("$accountId", accountId);
        lookup.Parameters.AddWithValue("$folderId", folderId);
        lookup.Parameters.AddWithValue("$uidValidity", uidValidity);
        lookup.Parameters.AddWithValue("$uid", uid.ToString(CultureInfo.InvariantCulture));
        var messageId = (string?)await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (messageId is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await ReplaceFolderMessageFlagsAsync(
            connection, transaction, folderId, messageId, normalizedFlags, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<string>> GetRemoteMessageFlagsAsync(
        string accountId,
        string folderId,
        string uidValidity,
        ulong uid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.flag
            FROM message_remote_identities ri
            JOIN folder_message_flags f
              ON f.folder_id = ri.folder_id AND f.message_id = ri.message_id
            WHERE ri.account_id = $accountId AND ri.folder_id = $folderId
              AND ri.uid_validity = $uidValidity AND ri.uid = $uid
            ORDER BY f.flag COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$folderId", folderId);
        command.Parameters.AddWithValue("$uidValidity", uidValidity);
        command.Parameters.AddWithValue("$uid", uid.ToString(CultureInfo.InvariantCulture));
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<StoredSyncCursor?> GetSyncCursorAsync(
        string accountId,
        string folderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT uid_validity, highest_uid, updated_utc FROM sync_cursors WHERE account_id = $accountId AND folder_id = $folderId;";
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$folderId", folderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new StoredSyncCursor(
            accountId, folderId, reader.GetString(0), checked((ulong)reader.GetInt64(1)),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public async Task SaveSyncCursorAsync(
        StoredSyncCursor cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor.UidValidity);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAccountAndFolderAsync(
            connection, transaction, cursor.AccountId, cursor.FolderId, cancellationToken).ConfigureAwait(false);
        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText =
                "SELECT uid_validity, highest_uid FROM sync_cursors WHERE account_id = $accountId AND folder_id = $folderId;";
            existingCommand.Parameters.AddWithValue("$accountId", cursor.AccountId);
            existingCommand.Parameters.AddWithValue("$folderId", cursor.FolderId);
            await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                string.Equals(reader.GetString(0), cursor.UidValidity, StringComparison.Ordinal) &&
                cursor.HighestUid < checked((ulong)reader.GetInt64(1)))
                throw new InvalidOperationException("IMAP cursor cannot move backward without a UIDVALIDITY change.");
        }
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO sync_cursors(account_id, folder_id, uid_validity, highest_uid, updated_utc)
            VALUES ($accountId, $folderId, $uidValidity, $highestUid, $updatedUtc)
            ON CONFLICT(account_id, folder_id) DO UPDATE SET
                uid_validity=excluded.uid_validity,
                highest_uid=excluded.highest_uid,
                updated_utc=excluded.updated_utc;
            """,
            cancellationToken,
            ("$accountId", cursor.AccountId), ("$folderId", cursor.FolderId),
            ("$uidValidity", cursor.UidValidity), ("$highestUid", checked((long)cursor.HighestUid)),
            ("$updatedUtc", cursor.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TombstoneRemoteMessageAsync(
        string accountId,
        string folderId,
        string uidValidity,
        ulong uid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE folder_messages
            SET is_tombstoned = 1
            WHERE folder_id = $folderId AND message_id IN (
                SELECT message_id FROM message_remote_identities
                WHERE account_id = $accountId AND folder_id = $folderId
                  AND uid_validity = $uidValidity AND uid = $uid
            );
            """;
        command.Parameters.AddWithValue("$folderId", folderId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$uidValidity", uidValidity);
        command.Parameters.AddWithValue("$uid", uid.ToString(CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlySet<ulong>> GetRemoteUidsAsync(
        string accountId,
        string folderId,
        string uidValidity,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ri.uid
            FROM message_remote_identities ri
            JOIN folder_messages fm
              ON fm.folder_id = ri.folder_id AND fm.message_id = ri.message_id
            WHERE ri.account_id = $accountId AND ri.folder_id = $folderId
              AND ri.uid_validity = $uidValidity
              AND fm.is_tombstoned = 0;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$folderId", folderId);
        command.Parameters.AddWithValue("$uidValidity", uidValidity);
        var uids = new HashSet<ulong>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ulong.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, out var uid)) uids.Add(uid);
        }
        return uids;
    }

    public async Task MarkMessageDeletedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE messages SET is_tombstoned = 1, tombstoned_utc = $now WHERE message_id = $messageId;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$messageId", messageId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException("Message was not found for tombstoning.");
        }
    }

    public async Task<OperationRegistration> RegisterOperationAsync(
        string idempotencyKey,
        string operationType,
        string payloadSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadSha256);
        var operationId = Guid.NewGuid().ToString("N");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO pending_operations(operation_id, idempotency_key, operation_type, payload_sha256, created_utc)
            VALUES ($operationId, $idempotencyKey, $operationType, $payloadSha256, $createdUtc)
            ON CONFLICT(idempotency_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        command.Parameters.AddWithValue("$operationType", operationType);
        command.Parameters.AddWithValue("$payloadSha256", payloadSha256);
        command.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (!inserted)
        {
            await using var query = connection.CreateCommand();
            query.CommandText =
                "SELECT operation_id, operation_type, payload_sha256 FROM pending_operations WHERE idempotency_key = $key;";
            query.Parameters.AddWithValue("$key", idempotencyKey);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Idempotent operation lookup failed.");
            if (!string.Equals(reader.GetString(1), operationType, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), payloadSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Idempotency key was reused for a different operation payload.");
            operationId = reader.GetString(0);
        }
        return new OperationRegistration(operationId, idempotencyKey, operationType, payloadSha256, !inserted);
    }

    public async Task AppendOperationEventAsync(
        string operationId,
        OperationEventState state,
        string? serverAcknowledgement = null,
        string? errorCode = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO operation_journal(operation_id, state, event_utc, server_acknowledgement, error_code)
            VALUES ($operationId, $state, $eventUtc, $serverAcknowledgement, $errorCode);
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$eventUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$serverAcknowledgement", (object?)serverAcknowledgement ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<QueuedOutboundMessage> QueueOutboundMessageAsync(
        string accountId,
        string idempotencyKey,
        string messageId,
        ReadOnlyMemory<byte> rawMime,
        CancellationToken cancellationToken = default)
        => await QueueOutboundMessageAsync(
            accountId, idempotencyKey, messageId, rawMime, envelopeSender: null,
            envelopeRecipients: null, cancellationToken).ConfigureAwait(false);

    public async Task<QueuedOutboundMessage> QueueOutboundMessageAsync(
        string accountId,
        string idempotencyKey,
        string messageId,
        ReadOnlyMemory<byte> rawMime,
        string? envelopeSender,
        IReadOnlyList<string>? envelopeRecipients,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(accountId, 64, nameof(accountId));
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ValidateRawMimeLength(rawMime.Length, "Outbound MIME");
        var normalizedEnvelope = NormalizeEnvelope(envelopeSender, envelopeRecipients);
        var stored = await _content.ImportBytesAsync(rawMime, "mime", cancellationToken).ConfigureAwait(false);
        var operation = await RegisterOperationAsync(
            idempotencyKey, "SendMail", stored.Sha256, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            "INSERT OR IGNORE INTO accounts(account_id) VALUES ($accountId);",
            cancellationToken,
            ("$accountId", accountId)).ConfigureAwait(false);
        await EnsureContentBlobAsync(
            connection, transaction, stored, "outbound-rfc-mime", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO outbound_messages(
                operation_id, account_id, message_id, raw_mime_sha256, raw_mime_bytes,
                envelope_sender, envelope_recipients)
            VALUES ($operationId, $accountId, $messageId, $sha256, $byteCount, $envelopeSender, $envelopeRecipients)
            ON CONFLICT(operation_id) DO NOTHING;
            """,
            cancellationToken,
            ("$operationId", operation.OperationId), ("$messageId", messageId),
            ("$accountId", accountId),
            ("$sha256", stored.Sha256), ("$byteCount", stored.Bytes),
            ("$envelopeSender", normalizedEnvelope.Sender),
            ("$envelopeRecipients", normalizedEnvelope.SerializedRecipients)).ConfigureAwait(false);
        await using (var verification = connection.CreateCommand())
        {
            verification.Transaction = transaction;
            verification.CommandText =
                """
                SELECT account_id, message_id, raw_mime_sha256, raw_mime_bytes,
                       envelope_sender, envelope_recipients
                FROM outbound_messages WHERE operation_id = $operationId;
                """;
            verification.Parameters.AddWithValue("$operationId", operation.OperationId);
            await using var reader = await verification.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                !string.Equals(reader.GetString(0), accountId, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(1), messageId, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), stored.Sha256, StringComparison.Ordinal) ||
                reader.GetInt64(3) != stored.Bytes ||
                !string.Equals(reader.GetString(4), normalizedEnvelope.Sender, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(5), normalizedEnvelope.SerializedRecipients, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Idempotency key was reused for a different outbound account, MIME, or SMTP envelope route.");
        }
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO operation_journal(operation_id, state, event_utc)
            SELECT $operationId, 'Queued', $eventUtc
            WHERE NOT EXISTS (SELECT 1 FROM operation_journal WHERE operation_id = $operationId);
            """,
            cancellationToken,
            ("$operationId", operation.OperationId),
            ("$eventUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new QueuedOutboundMessage(
            operation.OperationId, operation.IdempotencyKey, accountId, messageId, stored.Sha256, stored.Bytes,
            normalizedEnvelope.Sender, normalizedEnvelope.Recipients);
    }

    public async Task<IReadOnlyList<QueuedOutboundMessage>> ReadReadyOutboundMessagesAsync(
        string accountId,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(accountId, 64, nameof(accountId));
        if (accountId.Equals(
                MailAccountProfilePolicy.LocalOutboxStagingAccountId,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The local Outbox staging identity is permanently ineligible for dispatch.");
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        var messages = new List<QueuedOutboundMessage>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.operation_id, p.idempotency_key, o.account_id, o.message_id, o.raw_mime_sha256,
                   o.raw_mime_bytes, o.envelope_sender, o.envelope_recipients
            FROM pending_operations p
            JOIN outbound_messages o ON o.operation_id = p.operation_id
            JOIN operation_journal latest ON latest.sequence = (
                SELECT MAX(j.sequence) FROM operation_journal j WHERE j.operation_id = p.operation_id)
            WHERE o.account_id=$accountId AND latest.state IN ('Queued', 'RetryScheduled')
            ORDER BY latest.sequence
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        command.Parameters.AddWithValue("$accountId", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new QueuedOutboundMessage(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5), reader.GetString(6),
                DeserializeEnvelopeRecipients(reader.GetString(7))));
        }
        return messages;
    }

    public async Task<IReadOnlyList<StoredOutboundOperationSummary>> ReadOutboundOperationSummariesAsync(
        int maximumCount = 250,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 1_000);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.operation_id, p.idempotency_key, o.account_id, o.message_id, o.raw_mime_sha256,
                   o.raw_mime_bytes, latest.state, latest.event_utc,
                   latest.server_acknowledgement, latest.error_code
            FROM pending_operations p
            JOIN outbound_messages o ON o.operation_id = p.operation_id
            JOIN operation_journal latest ON latest.sequence = (
                SELECT MAX(j.sequence) FROM operation_journal j WHERE j.operation_id = p.operation_id)
            ORDER BY latest.sequence DESC
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        var operations = new List<StoredOutboundOperationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Enum.TryParse<OperationEventState>(reader.GetString(6), ignoreCase: false, out var state))
                throw new InvalidDataException("The outbound journal contains an unknown operation state.");
            operations.Add(new StoredOutboundOperationSummary(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5), state,
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return operations;
    }

    public async Task<StoredSentMessageProjection> AcknowledgeOutboundAndProjectSentAsync(
        string operationId,
        SentMessageProjectionInput projection,
        string redactedAcknowledgement,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(operationId, 64, nameof(operationId));
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentException.ThrowIfNullOrWhiteSpace(projection.RfcMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redactedAcknowledgement);
        if (redactedAcknowledgement.Length > 512 ||
            redactedAcknowledgement.Any(character => character is '\r' or '\n' or '\0'))
            throw new InvalidDataException("The outbound acknowledgement description is invalid.");

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        string accountId;
        string queuedMessageId;
        string rawMimeSha256;
        long rawMimeBytes;
        OperationEventState latestState;
        await using (var queued = connection.CreateCommand())
        {
            queued.Transaction = transaction;
            queued.CommandText =
                """
                SELECT o.account_id, o.message_id, o.raw_mime_sha256, o.raw_mime_bytes, latest.state
                FROM outbound_messages o
                JOIN operation_journal latest ON latest.sequence=(
                    SELECT MAX(j.sequence) FROM operation_journal j WHERE j.operation_id=o.operation_id)
                WHERE o.operation_id=$operationId;
                """;
            queued.Parameters.AddWithValue("$operationId", operationId);
            await using var reader = await queued.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new KeyNotFoundException("The outbound operation was not found.");
            if (reader.IsDBNull(0))
                throw new InvalidDataException(
                    "An outbound operation created before account binding cannot be acknowledged or sent.");
            accountId = reader.GetString(0);
            queuedMessageId = reader.GetString(1);
            rawMimeSha256 = reader.GetString(2);
            rawMimeBytes = reader.GetInt64(3);
            if (!Enum.TryParse(reader.GetString(4), ignoreCase: false, out latestState))
                throw new InvalidDataException("The outbound operation has an unknown journal state.");
        }
        if (!NormalizeRfcMessageId(queuedMessageId).Equals(
                NormalizeRfcMessageId(projection.RfcMessageId), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The sent projection Message-ID differs from its immutable outbound operation.");

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText =
                """
                SELECT p.message_id, m.account_id, m.raw_mime_sha256, m.raw_mime_bytes,
                       fm.folder_id, fm.is_tombstoned, m.is_tombstoned
                FROM sent_outbound_projections p
                JOIN messages m ON m.message_id=p.message_id
                JOIN folder_messages fm ON fm.message_id=m.message_id
                JOIN folders f ON f.folder_id=fm.folder_id AND f.is_sent_items=1
                WHERE p.operation_id=$operationId;
                """;
            existing.Parameters.AddWithValue("$operationId", operationId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (latestState != OperationEventState.Acknowledged ||
                    !reader.GetString(1).Equals(accountId, StringComparison.Ordinal) ||
                    !reader.GetString(2).Equals(rawMimeSha256, StringComparison.Ordinal) ||
                    reader.GetInt64(3) != rawMimeBytes || reader.GetInt64(5) != 0 || reader.GetInt64(6) != 0)
                    throw new InvalidDataException(
                        "The retained Sent Items projection conflicts with its outbound acknowledgement.");
                var retained = new StoredSentMessageProjection(
                    operationId, accountId, reader.GetString(4), reader.GetString(0),
                    rawMimeSha256, rawMimeBytes, WasAlreadyPresent: true);
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return retained;
            }
        }
        if (latestState != OperationEventState.Attempting)
            throw new InvalidOperationException(
                "Only an outbound operation with an active submission attempt can be acknowledged into Sent Items.");

        var sentFolderId = BuildSentItemsFolderId(accountId);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO folders(folder_id, account_id, display_name, is_sent_items)
            VALUES ($folderId, $accountId, 'Sent Items', 1)
            ON CONFLICT(folder_id) DO NOTHING;
            """,
            cancellationToken,
            ("$folderId", sentFolderId), ("$accountId", accountId)).ConfigureAwait(false);
        await using (var folder = connection.CreateCommand())
        {
            folder.Transaction = transaction;
            folder.CommandText =
                """
                SELECT account_id, display_name, is_sent_items, is_deleted_items,
                       is_local_only, remote_full_name, is_tombstoned
                FROM folders WHERE folder_id=$folderId;
                """;
            folder.Parameters.AddWithValue("$folderId", sentFolderId);
            await using var reader = await folder.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                !reader.GetString(0).Equals(accountId, StringComparison.Ordinal) ||
                !reader.GetString(1).Equals("Sent Items", StringComparison.Ordinal) ||
                reader.GetInt64(2) != 1 || reader.GetInt64(3) != 0 || reader.GetInt64(4) != 1 ||
                !reader.IsDBNull(5) || reader.GetInt64(6) != 0)
                throw new InvalidDataException("The account's retained Sent Items folder binding conflicts.");
        }

        var messageId = Guid.NewGuid().ToString("N");
        var identityKey = $"sent-operation:{operationId}";
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO messages(
                message_id, account_id, identity_key, rfc_message_id, subject,
                raw_mime_sha256, raw_mime_bytes, imported_utc)
            VALUES ($messageId, $accountId, $identityKey, $rfcMessageId, $subject,
                    $sha256, $byteCount, $importedUtc);
            """,
            cancellationToken,
            ("$messageId", messageId), ("$accountId", accountId), ("$identityKey", identityKey),
            ("$rfcMessageId", projection.RfcMessageId), ("$subject", projection.Subject ?? string.Empty),
            ("$sha256", rawMimeSha256), ("$byteCount", rawMimeBytes),
            ("$importedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)))
            .ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO folder_messages(folder_id, message_id) VALUES ($folderId, $messageId);",
            cancellationToken,
            ("$folderId", sentFolderId), ("$messageId", messageId)).ConfigureAwait(false);
        await StoreNormalizedBodyAsync(
            connection, transaction, messageId, projection.From ?? string.Empty, projection.To ?? string.Empty,
            projection.SentUtc, projection.TextBody, projection.SanitizableHtmlBody, cancellationToken)
            .ConfigureAwait(false);
        await IndexSearchAsync(
            connection, transaction, messageId, projection.Subject ?? string.Empty,
            projection.From ?? string.Empty, projection.To ?? string.Empty, projection.TextBody, cancellationToken)
            .ConfigureAwait(false);
        await StoreMessageAttachmentsAsync(
            connection, transaction, messageId, projection.Attachments,
            "sent-attachment", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO sent_outbound_projections(operation_id, message_id, projected_utc)
            VALUES ($operationId, $messageId, $projectedUtc);
            """,
            cancellationToken,
            ("$operationId", operationId), ("$messageId", messageId),
            ("$projectedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)))
            .ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO operation_journal(operation_id, state, event_utc, server_acknowledgement)
            VALUES ($operationId, 'Acknowledged', $eventUtc, $acknowledgement);
            """,
            cancellationToken,
            ("$operationId", operationId),
            ("$eventUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            ("$acknowledgement", redactedAcknowledgement)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoredSentMessageProjection(
            operationId, accountId, sentFolderId, messageId, rawMimeSha256, rawMimeBytes,
            WasAlreadyPresent: false);
    }

    public async Task<StoredSentItemsProjectionEvidence?> ReadSentItemsProjectionEvidenceAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(operationId, 64, nameof(operationId));
        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.operation_id, o.account_id, fm.folder_id, p.message_id,
                   m.raw_mime_sha256, m.raw_mime_bytes, latest.state,
                   o.account_id IS NOT NULL, f.is_sent_items,
                   fm.is_tombstoned=0, m.is_tombstoned=0,
                   o.raw_mime_sha256=m.raw_mime_sha256 AND o.raw_mime_bytes=m.raw_mime_bytes
            FROM sent_outbound_projections p
            JOIN outbound_messages o ON o.operation_id=p.operation_id
            JOIN messages m ON m.message_id=p.message_id
            JOIN folder_messages fm ON fm.message_id=m.message_id
            JOIN folders f ON f.folder_id=fm.folder_id
            JOIN operation_journal latest ON latest.sequence=(
                SELECT MAX(j.sequence) FROM operation_journal j WHERE j.operation_id=p.operation_id)
            WHERE p.operation_id=$operationId AND f.is_sent_items=1;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        if (!Enum.TryParse<OperationEventState>(reader.GetString(6), ignoreCase: false, out var state))
            throw new InvalidDataException("The Sent Items projection has an unknown journal state.");
        var evidence = new StoredSentItemsProjectionEvidence(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5), state,
            reader.GetInt64(7) != 0, reader.GetInt64(8) != 0, reader.GetInt64(9) != 0,
            reader.GetInt64(10) != 0, reader.GetInt64(11) != 0);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The outbound operation has multiple Sent Items projections.");
        return evidence;
    }

    public async Task<StoredMailboxMutationOperation> QueueMailboxMutationAsync(
        string idempotencyKey,
        OfflineMailboxMutationIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(intent);
        ValidateMailboxMutationIntent(intent);
        var normalizedFlag = intent.Flag?.Trim();
        var payload = string.Join('\u001f',
            intent.AccountId,
            intent.MessageId,
            intent.SourceFolderId,
            intent.Kind.ToString(),
            intent.TargetFolderId ?? string.Empty,
            normalizedFlag ?? string.Empty);
        var payloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
        var operationId = Guid.NewGuid().ToString("N");
        var createdUtc = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var inserted = await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO pending_operations(operation_id, idempotency_key, operation_type, payload_sha256, created_utc)
            VALUES ($operationId, $idempotencyKey, 'MailboxMutation', $payloadSha256, $createdUtc)
            ON CONFLICT(idempotency_key) DO NOTHING;
            """,
            cancellationToken,
            ("$operationId", operationId),
            ("$idempotencyKey", idempotencyKey),
            ("$payloadSha256", payloadSha256),
            ("$createdUtc", createdUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false) == 1;
        if (!inserted)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await ReadMailboxMutationByIdempotencyKeyAsync(
                idempotencyKey, payloadSha256, cancellationToken).ConfigureAwait(false);
        }

        await using (var source = connection.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText =
                """
                SELECT f.is_deleted_items,
                       (SELECT COUNT(*)
                        FROM folder_messages active_link
                        JOIN folders active_folder
                          ON active_folder.folder_id=active_link.folder_id
                         AND active_folder.account_id=m.account_id
                        WHERE active_link.message_id=m.message_id
                          AND active_link.is_tombstoned=0
                          AND active_folder.is_tombstoned=0)
                FROM messages m
                JOIN folder_messages fm ON fm.message_id=m.message_id
                JOIN folders f ON f.folder_id=fm.folder_id AND f.account_id=m.account_id
                WHERE m.message_id=$messageId AND m.account_id=$accountId
                  AND fm.folder_id=$sourceFolderId AND fm.is_tombstoned=0
                  AND m.is_tombstoned=0 AND f.is_tombstoned=0;
                """;
            source.Parameters.AddWithValue("$messageId", intent.MessageId);
            source.Parameters.AddWithValue("$accountId", intent.AccountId);
            source.Parameters.AddWithValue("$sourceFolderId", intent.SourceFolderId);
            await using var reader = await source.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException(
                    "Mailbox mutation source is missing, tombstoned, or belongs to another account.");
            var sourceDeletedItemsMarker = reader.GetInt64(0);
            var activeFolderLinks = reader.GetInt64(1);
            if (intent.Kind == MailboxMutationKind.RetainDeletedHistory && sourceDeletedItemsMarker != 1)
                throw new InvalidDataException(
                    "Retained-history deletion is valid only from the designated Deleted Items folder.");
            if (intent.Kind == MailboxMutationKind.RetainDeletedHistory && activeFolderLinks != 1)
                throw new InvalidDataException(
                    "Retained-history deletion cannot hide another active folder link for the same message.");
        }

        if (intent.TargetFolderId is not null)
        {
            await using var target = connection.CreateCommand();
            target.Transaction = transaction;
            target.CommandText =
                "SELECT is_deleted_items FROM folders WHERE folder_id=$folderId AND account_id=$accountId AND is_tombstoned=0;";
            target.Parameters.AddWithValue("$folderId", intent.TargetFolderId);
            target.Parameters.AddWithValue("$accountId", intent.AccountId);
            var deletedItems = await target.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (deletedItems is not long marker)
                throw new InvalidDataException(
                    "Mailbox mutation target is missing, tombstoned, or belongs to another account.");
            if (intent.Kind == MailboxMutationKind.MoveToDeletedItems && marker != 1)
                throw new InvalidDataException(
                    "Permanent deletion must target a designated Deleted Items folder.");
        }

        string? expectedUidValidity = null;
        ulong? expectedUid = null;
        await using (var remoteIdentity = connection.CreateCommand())
        {
            remoteIdentity.Transaction = transaction;
            remoteIdentity.CommandText =
                """
                SELECT ri.uid_validity, ri.uid
                FROM message_remote_identities ri
                JOIN sync_cursors sc
                  ON sc.account_id=ri.account_id AND sc.folder_id=ri.folder_id
                 AND sc.uid_validity=ri.uid_validity
                WHERE ri.account_id=$accountId AND ri.folder_id=$folderId AND ri.message_id=$messageId
                ORDER BY CAST(ri.uid AS INTEGER) DESC
                LIMIT 1;
                """;
            remoteIdentity.Parameters.AddWithValue("$accountId", intent.AccountId);
            remoteIdentity.Parameters.AddWithValue("$folderId", intent.SourceFolderId);
            remoteIdentity.Parameters.AddWithValue("$messageId", intent.MessageId);
            await using var reader = await remoteIdentity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expectedUidValidity = reader.GetString(0);
                expectedUid = ulong.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            }
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO mailbox_mutation_operations(
                operation_id, account_id, message_id, source_folder_id, mutation_kind,
                target_folder_id, flag_name, expected_uid_validity, expected_uid)
            VALUES (
                $operationId, $accountId, $messageId, $sourceFolderId, $mutationKind,
                $targetFolderId, $flagName, $expectedUidValidity, $expectedUid);
            """,
            cancellationToken,
            ("$operationId", operationId),
            ("$accountId", intent.AccountId),
            ("$messageId", intent.MessageId),
            ("$sourceFolderId", intent.SourceFolderId),
            ("$mutationKind", intent.Kind.ToString()),
            ("$targetFolderId", (object?)intent.TargetFolderId ?? DBNull.Value),
            ("$flagName", (object?)normalizedFlag ?? DBNull.Value),
            ("$expectedUidValidity", (object?)expectedUidValidity ?? DBNull.Value),
            ("$expectedUid", expectedUid is null
                ? DBNull.Value
                : expectedUid.Value.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);

        switch (intent.Kind)
        {
            case MailboxMutationKind.MoveToFolder:
            case MailboxMutationKind.MoveToDeletedItems:
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO folder_messages(folder_id, message_id, is_tombstoned)
                    VALUES ($targetFolderId, $messageId, 0)
                    ON CONFLICT(folder_id, message_id) DO UPDATE SET is_tombstoned=0;
                    INSERT OR IGNORE INTO folder_message_flags(folder_id, message_id, flag)
                    SELECT $targetFolderId, message_id, flag
                    FROM folder_message_flags
                    WHERE folder_id=$sourceFolderId AND message_id=$messageId;
                    UPDATE folder_messages SET is_tombstoned=1
                    WHERE folder_id=$sourceFolderId AND message_id=$messageId;
                    """,
                    cancellationToken,
                    ("$targetFolderId", intent.TargetFolderId!),
                    ("$sourceFolderId", intent.SourceFolderId),
                    ("$messageId", intent.MessageId)).ConfigureAwait(false);
                break;
            case MailboxMutationKind.SetFlag:
                await ExecuteAsync(
                    connection,
                    transaction,
                    "INSERT OR IGNORE INTO folder_message_flags(folder_id, message_id, flag) VALUES ($folderId, $messageId, $flag);",
                    cancellationToken,
                    ("$folderId", intent.SourceFolderId),
                    ("$messageId", intent.MessageId),
                    ("$flag", normalizedFlag!)).ConfigureAwait(false);
                break;
            case MailboxMutationKind.ClearFlag:
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM folder_message_flags WHERE folder_id=$folderId AND message_id=$messageId AND flag=$flag;",
                    cancellationToken,
                    ("$folderId", intent.SourceFolderId),
                    ("$messageId", intent.MessageId),
                    ("$flag", normalizedFlag!)).ConfigureAwait(false);
                break;
            case MailboxMutationKind.RetainDeletedHistory:
                if (await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE folder_messages SET is_tombstoned=1 " +
                    "WHERE folder_id=$sourceFolderId AND message_id=$messageId AND is_tombstoned=0;",
                    cancellationToken,
                    ("$sourceFolderId", intent.SourceFolderId),
                    ("$messageId", intent.MessageId)).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException(
                        "Deleted Items source link changed before its retained-history tombstone was saved.");
                if (await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE messages SET is_tombstoned=1, tombstoned_utc=$tombstonedUtc " +
                    "WHERE message_id=$messageId AND account_id=$accountId AND is_tombstoned=0;",
                    cancellationToken,
                    ("$messageId", intent.MessageId),
                    ("$accountId", intent.AccountId),
                    ("$tombstonedUtc", createdUtc.ToString("O", CultureInfo.InvariantCulture)))
                    .ConfigureAwait(false) != 1)
                    throw new InvalidOperationException(
                        "Deleted Items message changed before its retained-history tombstone was saved.");
                break;
        }
        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO operation_journal(operation_id, state, event_utc) VALUES ($operationId, 'Queued', $eventUtc);",
            cancellationToken,
            ("$operationId", operationId),
            ("$eventUtc", createdUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoredMailboxMutationOperation(
            operationId, idempotencyKey, intent.AccountId, intent.MessageId, intent.SourceFolderId,
            intent.Kind, intent.TargetFolderId, normalizedFlag, expectedUidValidity, expectedUid,
            OperationEventState.Queued, createdUtc, null, null, WasAlreadyPresent: false);
    }

    public async Task<IReadOnlyList<StoredMailboxMutationOperation>> ReadMailboxMutationOperationsAsync(
        int maximumCount = 250,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 1_000);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.operation_id, p.idempotency_key, m.account_id, m.message_id,
                   m.source_folder_id, m.mutation_kind, m.target_folder_id, m.flag_name,
                   m.expected_uid_validity, m.expected_uid, latest.state, latest.event_utc,
                   latest.server_acknowledgement, latest.error_code
            FROM pending_operations p
            JOIN mailbox_mutation_operations m ON m.operation_id=p.operation_id
            JOIN operation_journal latest ON latest.sequence=(
                SELECT MAX(j.sequence) FROM operation_journal j WHERE j.operation_id=p.operation_id)
            ORDER BY latest.sequence DESC
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        var operations = new List<StoredMailboxMutationOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            operations.Add(ReadMailboxMutation(reader, wasAlreadyPresent: false));
        return operations;
    }

    public async Task<StoredDeletedItemsRetentionEvidence?> ReadDeletedItemsRetentionEvidenceAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(operationId, 128, nameof(operationId));
        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT operation.operation_id, operation.account_id, operation.message_id,
                   operation.source_folder_id, folder.is_deleted_items,
                   link.is_tombstoned, message.is_tombstoned,
                   message.raw_mime_sha256, message.raw_mime_bytes, latest.state
            FROM mailbox_mutation_operations operation
            JOIN folders folder
              ON folder.folder_id=operation.source_folder_id
             AND folder.account_id=operation.account_id
            JOIN folder_messages link
              ON link.folder_id=operation.source_folder_id
             AND link.message_id=operation.message_id
            JOIN messages message
              ON message.message_id=operation.message_id
             AND message.account_id=operation.account_id
            JOIN operation_journal latest ON latest.sequence=(
                SELECT MAX(journal.sequence) FROM operation_journal journal
                WHERE journal.operation_id=operation.operation_id)
            WHERE operation.operation_id=$operationId
              AND operation.mutation_kind='RetainDeletedHistory'
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        if (!Enum.TryParse<OperationEventState>(reader.GetString(9), ignoreCase: false, out var state))
            throw new InvalidDataException("Deleted Items retention journal contains an unknown state.");
        return new StoredDeletedItemsRetentionEvidence(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4) != 0, reader.GetInt64(5) != 0, reader.GetInt64(6) != 0,
            reader.GetString(7), reader.GetInt64(8), state);
    }

    public async Task<IReadOnlyList<StoredMailboxMutationOperation>> ReadReadyMailboxMutationOperationsAsync(
        string accountId,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(accountId, 64, nameof(accountId));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 100);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.operation_id, p.idempotency_key, m.account_id, m.message_id,
                   m.source_folder_id, m.mutation_kind, m.target_folder_id, m.flag_name,
                   m.expected_uid_validity, m.expected_uid, latest.state, latest.event_utc,
                   latest.server_acknowledgement, latest.error_code
            FROM pending_operations p
            JOIN mailbox_mutation_operations m ON m.operation_id=p.operation_id
            JOIN operation_journal latest ON latest.sequence=(
                SELECT MAX(j.sequence) FROM operation_journal j WHERE j.operation_id=p.operation_id)
            WHERE m.account_id=$accountId AND latest.state IN ('Queued','RetryScheduled')
            ORDER BY latest.sequence
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        var operations = new List<StoredMailboxMutationOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            operations.Add(ReadMailboxMutation(reader, wasAlreadyPresent: false));
        return operations;
    }

    public async Task<byte[]> ReadOutboundMimeAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT b.relative_path, o.raw_mime_sha256, o.raw_mime_bytes
            FROM outbound_messages o
            JOIN content_blobs b ON b.sha256 = o.raw_mime_sha256
            WHERE o.operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        string relativePath;
        string expectedSha256;
        long expectedBytes;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new KeyNotFoundException("Outbound operation was not found.");
            relativePath = reader.GetString(0);
            expectedSha256 = reader.GetString(1);
            expectedBytes = reader.GetInt64(2);
        }
        return await ReadVerifiedContentAsync(
            relativePath, expectedSha256, expectedBytes, "Outbound MIME", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StoredDraftRevision> SaveDraftRevisionAsync(
        DraftRevisionImport import,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(import);
        ValidateRawMimeLength(import.RawMime.Length, "Draft MIME");
        ValidateDraftField(import.Subject, 998, nameof(import.Subject));
        ValidateDraftField(import.Sender, 2_048, nameof(import.Sender));
        ValidateDraftField(import.Recipients, 16_384, nameof(import.Recipients));
        ValidateDraftField(import.BodySnippet, 4_096, nameof(import.BodySnippet));
        var draftId = string.IsNullOrWhiteSpace(import.DraftId)
            ? Guid.NewGuid().ToString("N")
            : import.DraftId;
        if (!Guid.TryParseExact(draftId, "N", out _))
            throw new InvalidDataException("Draft identifier must be a 32-character GUID.");

        var stored = await _content.ImportBytesAsync(import.RawMime, "mime", cancellationToken)
            .ConfigureAwait(false);
        var savedUtc = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureContentBlobAsync(
            connection, transaction, stored, "draft-rfc-mime", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO drafts(draft_id, created_utc)
            VALUES ($draftId, $createdUtc)
            ON CONFLICT(draft_id) DO NOTHING;
            """,
            cancellationToken,
            ("$draftId", draftId),
            ("$createdUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);

        await using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText =
            "SELECT is_tombstoned, COALESCE((SELECT MAX(revision) FROM draft_revisions WHERE draft_id = $draftId), 0) FROM drafts WHERE draft_id = $draftId;";
        state.Parameters.AddWithValue("$draftId", draftId);
        await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Draft could not be created or found.");
        if (reader.GetInt64(0) != 0)
            throw new InvalidOperationException("A tombstoned draft cannot receive new revisions.");
        var revision = checked(reader.GetInt32(1) + 1);
        await reader.DisposeAsync().ConfigureAwait(false);

        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO draft_revisions(
                draft_id, revision, raw_mime_sha256, raw_mime_bytes,
                subject, sender, recipients, body_snippet, saved_utc)
            VALUES (
                $draftId, $revision, $sha256, $byteCount,
                $subject, $sender, $recipients, $bodySnippet, $savedUtc);
            """,
            cancellationToken,
            ("$draftId", draftId), ("$revision", revision),
            ("$sha256", stored.Sha256), ("$byteCount", stored.Bytes),
            ("$subject", import.Subject), ("$sender", import.Sender),
            ("$recipients", import.Recipients), ("$bodySnippet", import.BodySnippet),
            ("$savedUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoredDraftRevision(
            draftId, revision, stored.Sha256, stored.Bytes,
            import.Subject, import.Sender, import.Recipients, import.BodySnippet,
            savedUtc, IsTombstoned: false);
    }

    public async Task<IReadOnlyList<StoredDraftRevision>> ReadLatestDraftsAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 250);
        var drafts = new List<StoredDraftRevision>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.draft_id, r.revision, r.raw_mime_sha256, r.raw_mime_bytes,
                   r.subject, r.sender, r.recipients, r.body_snippet, r.saved_utc,
                   d.is_tombstoned
            FROM draft_revisions r
            JOIN drafts d ON d.draft_id = r.draft_id
            WHERE r.revision = (
                SELECT MAX(latest.revision) FROM draft_revisions latest
                WHERE latest.draft_id = r.draft_id)
              AND d.is_tombstoned = 0
            ORDER BY r.saved_utc DESC, r.draft_id
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            drafts.Add(ReadDraftRevision(reader));
        }
        return drafts;
    }

    public async Task<byte[]> ReadDraftMimeAsync(
        string draftId,
        int revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT b.relative_path, r.raw_mime_sha256, r.raw_mime_bytes
            FROM draft_revisions r
            JOIN content_blobs b ON b.sha256 = r.raw_mime_sha256
            WHERE r.draft_id = $draftId AND r.revision = $revision;
            """;
        command.Parameters.AddWithValue("$draftId", draftId);
        command.Parameters.AddWithValue("$revision", revision);
        string relativePath;
        string expectedSha256;
        long expectedBytes;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new KeyNotFoundException("Draft revision was not found.");
            relativePath = reader.GetString(0);
            expectedSha256 = reader.GetString(1);
            expectedBytes = reader.GetInt64(2);
        }
        return await ReadVerifiedContentAsync(
            relativePath, expectedSha256, expectedBytes, "Draft MIME", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StoredDraftRevisionEvidence?> ReadDraftRevisionEvidenceAsync(
        string draftId,
        int revision,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(draftId, "N", out _))
            throw new InvalidDataException("Draft identifier must be a 32-character GUID.");
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.draft_id, r.revision, r.raw_mime_sha256, r.raw_mime_bytes,
                   r.saved_utc,
                   r.revision=(SELECT MAX(latest.revision) FROM draft_revisions latest
                               WHERE latest.draft_id=r.draft_id),
                   d.is_tombstoned
            FROM draft_revisions r
            JOIN drafts d ON d.draft_id=r.draft_id
            WHERE r.draft_id=$draftId AND r.revision=$revision;
            """;
        command.Parameters.AddWithValue("$draftId", draftId);
        command.Parameters.AddWithValue("$revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var evidence = new StoredDraftRevisionEvidence(
            reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt64(5) != 0, reader.GetInt64(6) != 0);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Draft revision evidence is not unique.");
        return evidence;
    }

    public async Task TombstoneDraftAsync(
        string draftId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE drafts SET is_tombstoned = 1, tombstoned_utc = $now WHERE draft_id = $draftId AND is_tombstoned = 0;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$draftId", draftId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new KeyNotFoundException("Active draft was not found for tombstoning.");
    }

    public async Task<int> MarkInterruptedOutboundAttemptsAsConflictsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO operation_journal(operation_id, state, event_utc, error_code)
            SELECT o.operation_id, 'Conflict', $eventUtc, 'InterruptedSubmissionUnknown'
            FROM outbound_messages o
            JOIN operation_journal latest ON latest.sequence = (
                SELECT MAX(j.sequence) FROM operation_journal j WHERE j.operation_id = o.operation_id)
            WHERE latest.state = 'Attempting';
            """;
        command.Parameters.AddWithValue("$eventUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> MarkInterruptedMailboxMutationAttemptsAsConflictsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO operation_journal(operation_id, state, event_utc, error_code)
            SELECT m.operation_id, 'Conflict', $eventUtc, 'InterruptedMailboxMutationUnknown'
            FROM mailbox_mutation_operations m
            JOIN operation_journal latest ON latest.sequence = (
                SELECT MAX(j.sequence) FROM operation_journal j WHERE j.operation_id = m.operation_id)
            WHERE latest.state = 'Attempting';
            """;
        command.Parameters.AddWithValue("$eventUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationEventState?> GetLatestOperationStateAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT state FROM operation_journal WHERE operation_id = $operationId ORDER BY sequence DESC LIMIT 1;";
        command.Parameters.AddWithValue("$operationId", operationId);
        var value = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : Enum.Parse<OperationEventState>(value, ignoreCase: false);
    }

    public async Task<StoredMailAccountProfile> SaveMailAccountProfileRevisionAsync(
        MailAccountProfileInput profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateMailAccountProfile(profile);
        var savedUtc = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO accounts(account_id) VALUES ($accountId);",
            cancellationToken, ("$accountId", profile.AccountId)).ConfigureAwait(false);
        long revision;
        await using (var revisionCommand = connection.CreateCommand())
        {
            revisionCommand.Transaction = transaction;
            revisionCommand.CommandText =
                "SELECT COALESCE(MAX(revision), 0) + 1 FROM account_profile_revisions WHERE account_id = $accountId;";
            revisionCommand.Parameters.AddWithValue("$accountId", profile.AccountId);
            revision = (long)(await revisionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Account profile revision could not be allocated."));
        }
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO account_profile_revisions(
                account_id, revision, display_name, receive_protocol, receive_host, receive_port,
                receive_tls_mode, smtp_host, smtp_port, smtp_tls_mode, credential_user_name,
                sender_address, saved_utc)
            VALUES (
                $accountId, $revision, $displayName, $receiveProtocol, $receiveHost, $receivePort,
                $receiveTlsMode, $smtpHost, $smtpPort, $smtpTlsMode, $credentialUserName,
                $senderAddress, $savedUtc);
            """,
            cancellationToken,
            ("$accountId", profile.AccountId), ("$revision", revision),
            ("$displayName", profile.DisplayName), ("$receiveProtocol", profile.ReceiveProtocol),
            ("$receiveHost", profile.ReceiveHost), ("$receivePort", profile.ReceivePort),
            ("$receiveTlsMode", profile.ReceiveTlsMode), ("$smtpHost", profile.SmtpHost),
            ("$smtpPort", profile.SmtpPort), ("$smtpTlsMode", profile.SmtpTlsMode),
            ("$credentialUserName", profile.CredentialUserName),
            ("$senderAddress", profile.SenderAddress.Trim()),
            ("$savedUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoredMailAccountProfile(
            profile.AccountId, revision, profile.DisplayName, profile.ReceiveProtocol,
            profile.ReceiveHost, profile.ReceivePort, profile.ReceiveTlsMode,
            profile.SmtpHost, profile.SmtpPort, profile.SmtpTlsMode, profile.CredentialUserName,
            profile.SenderAddress.Trim(), savedUtc);
    }

    public async Task<IReadOnlyList<StoredMailAccountProfile>> ReadLatestMailAccountProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        const int maximumProfiles = 100;
        var profiles = new List<StoredMailAccountProfile>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.account_id, p.revision, p.display_name, p.receive_protocol, p.receive_host,
                p.receive_port, p.receive_tls_mode, p.smtp_host, p.smtp_port, p.smtp_tls_mode,
                p.credential_user_name, p.sender_address, p.saved_utc
            FROM account_profile_revisions p
            WHERE p.revision = (
                SELECT MAX(latest.revision)
                FROM account_profile_revisions latest
                WHERE latest.account_id = p.account_id)
            ORDER BY p.account_id
            LIMIT 101;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (profiles.Count == maximumProfiles)
                throw new InvalidDataException("Development account profile count exceeds its limit.");
            profiles.Add(new StoredMailAccountProfile(
                reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
                reader.GetInt32(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
                DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return profiles;
    }

    public async Task<LocalCalendarEventSaveResult> SaveLocalCalendarEventRevisionAsync(
        LocalCalendarEventRevisionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateLocalCalendarEventInput(input);
        var eventId = string.IsNullOrWhiteSpace(input.EventId)
            ? Guid.NewGuid().ToString("N")
            : NormalizeCalendarEventId(input.EventId);
        var title = input.Title.Trim();
        var location = input.Location.Trim();
        var evidenceId = input.EvidenceId?.ToLowerInvariant();
        var sourceMessageIdentitySha256 = input.SourceMessageIdentity is null
            ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.SourceMessageIdentity)));
        var providerAccountId = input.ProviderAccountId?.Trim();
        var providerEventId = input.ProviderEventId?.Trim();
        var providerSeriesId = input.ProviderSeriesId?.Trim();
        var savedUtc = DateTimeOffset.UtcNow;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        int revision;
        if (string.IsNullOrWhiteSpace(input.EventId))
        {
            if (evidenceId is not null)
            {
                await using var duplicate = connection.CreateCommand();
                duplicate.Transaction = transaction;
                duplicate.CommandText =
                    "SELECT event_id FROM calendar_event_sources WHERE evidence_id=$evidenceId;";
                duplicate.Parameters.AddWithValue("$evidenceId", evidenceId);
                var duplicateEventId = await duplicate.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false) as string;
                if (duplicateEventId is not null)
                {
                    var existing = await ReadCalendarEventRevisionAsync(
                        connection, transaction, duplicateEventId, revision: null, cancellationToken)
                        .ConfigureAwait(false);
                    if (existing.SourceKind != CalendarEventSourceKind.EmailSuggestion ||
                        !string.Equals(
                            existing.SourceMessageIdentitySha256,
                            sourceMessageIdentitySha256,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Calendar evidence identity is already bound to different immutable provenance.");
                    }
                    if (existing.IsTombstoned)
                        throw new InvalidOperationException(
                            "This evidence-bound calendar event was retired and cannot be recreated or resurrected.");
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new LocalCalendarEventSaveResult(existing, WasAlreadyPresent: true);
                }
            }

            await ExecuteAsync(
                connection, transaction,
                "INSERT INTO calendar_events(event_id, created_utc) VALUES ($eventId, $createdUtc);",
                cancellationToken,
                ("$eventId", eventId),
                ("$createdUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO calendar_event_sources(
                    event_id, source_kind, evidence_id, source_message_identity_sha256,
                    provider_account_id, provider_event_id, provider_series_id, created_utc)
                VALUES ($eventId, $sourceKind, $evidenceId, $sourceMessageIdentitySha256,
                    $providerAccountId, $providerEventId, $providerSeriesId, $createdUtc);
                """,
                cancellationToken,
                ("$eventId", eventId),
                ("$sourceKind", input.SourceKind.ToString()),
                ("$evidenceId", (object?)evidenceId ?? DBNull.Value),
                ("$sourceMessageIdentitySha256", (object?)sourceMessageIdentitySha256 ?? DBNull.Value),
                ("$providerAccountId", (object?)providerAccountId ?? DBNull.Value),
                ("$providerEventId", (object?)providerEventId ?? DBNull.Value),
                ("$providerSeriesId", (object?)providerSeriesId ?? DBNull.Value),
                ("$createdUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            revision = 1;
        }
        else
        {
            var current = await ReadCalendarEventRevisionAsync(
                connection, transaction, eventId, revision: null, cancellationToken).ConfigureAwait(false);
            if (current.IsTombstoned)
                throw new InvalidOperationException("A retired calendar event cannot be edited or resurrected.");
            if (current.SourceKind != input.SourceKind ||
                !string.Equals(current.EvidenceId, evidenceId, StringComparison.Ordinal) ||
                !string.Equals(
                    current.SourceMessageIdentitySha256,
                    sourceMessageIdentitySha256,
                    StringComparison.Ordinal) ||
                !string.Equals(current.ProviderAccountId, providerAccountId, StringComparison.Ordinal) ||
                !string.Equals(current.ProviderEventId, providerEventId, StringComparison.Ordinal) ||
                !string.Equals(current.ProviderSeriesId, providerSeriesId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Calendar event source provenance is immutable and cannot change between revisions.");
            }
            revision = checked(current.Revision + 1);
        }

        await InsertCalendarEventRevisionAsync(
            connection, transaction, eventId, revision, title,
            input.StartLocal, input.EndLocal, input.AllDay, location, input.Notes,
            isTombstoned: false, savedUtc, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var stored = await ReadCalendarEventRevisionAsync(
            eventId, revision, cancellationToken).ConfigureAwait(false);
        return new LocalCalendarEventSaveResult(stored, WasAlreadyPresent: false);
    }

    public async Task<StoredLocalCalendarEventRevision> TombstoneLocalCalendarEventAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        var normalizedEventId = NormalizeCalendarEventId(eventId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadCalendarEventRevisionAsync(
            connection, transaction, normalizedEventId, revision: null, cancellationToken)
            .ConfigureAwait(false);
        if (current.IsTombstoned)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }
        var revision = checked(current.Revision + 1);
        var savedUtc = DateTimeOffset.UtcNow;
        await InsertCalendarEventRevisionAsync(
            connection, transaction, normalizedEventId, revision, current.Title,
            current.StartLocal, current.EndLocal, current.AllDay, current.Location, current.Notes,
            isTombstoned: true, savedUtc, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCalendarEventRevisionAsync(
            normalizedEventId, revision, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CalendarProviderEventApplyResult> ApplyCalendarProviderEventSnapshotAsync(
        CalendarProviderEventSnapshotImport input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateCalendarProviderEventSnapshotImport(input);
        var sourceKind = input.SourceKind.ToString();
        var accountIdentity = input.ProviderAccountIdentitySha256;
        var eventIdentity = input.ProviderEventIdentitySha256;
        var revisionIdentity = input.ProviderRevisionIdentitySha256;
        var seriesIdentity = input.ProviderSeriesIdentitySha256;
        var payloadSha256 = HashCalendarProviderEventSnapshot(input);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var repeated = connection.CreateCommand())
        {
            repeated.Transaction = transaction;
            repeated.CommandText =
                """
                SELECT payload_sha256, event_id, outcome, observed_utc,
                    provider_series_identity_sha256
                FROM calendar_provider_observations
                WHERE source_kind=$sourceKind
                  AND provider_account_identity_sha256=$accountIdentity
                  AND provider_event_identity_sha256=$eventIdentity
                  AND provider_revision_identity_sha256=$revisionIdentity;
                """;
            repeated.Parameters.AddWithValue("$sourceKind", sourceKind);
            repeated.Parameters.AddWithValue("$accountIdentity", accountIdentity);
            repeated.Parameters.AddWithValue("$eventIdentity", eventIdentity);
            repeated.Parameters.AddWithValue("$revisionIdentity", revisionIdentity);
            await using var reader = await repeated.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(0), payloadSha256, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "A provider revision identity was reused with different calendar content.");
                var storedSeriesIdentity = reader.IsDBNull(4) ? null : reader.GetString(4);
                if (!string.Equals(storedSeriesIdentity, seriesIdentity, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "A provider revision identity was reused with different calendar series provenance.");
                var storedEventId = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!Enum.TryParse<CalendarProviderObservationOutcome>(
                        reader.GetString(2), ignoreCase: false, out var repeatedOutcome))
                    throw new InvalidDataException("A stored calendar provider outcome is invalid.");
                var repeatedObservation = new StoredCalendarProviderObservation(
                    input.SourceKind,
                    accountIdentity,
                    eventIdentity,
                    revisionIdentity,
                    payloadSha256,
                    storedEventId,
                    repeatedOutcome,
                    DateTimeOffset.Parse(
                        reader.GetString(3), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                    storedSeriesIdentity);
                await reader.DisposeAsync().ConfigureAwait(false);
                var repeatedEvent = storedEventId is null
                    ? null
                    : await ReadCalendarEventRevisionAsync(
                        connection, transaction, storedEventId, revision: null, cancellationToken)
                        .ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new CalendarProviderEventApplyResult(
                    repeatedObservation, repeatedEvent, WasAlreadyObserved: true);
            }
        }

        string? eventId;
        await using (var source = connection.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText =
                """
                SELECT event_id, provider_series_id FROM calendar_event_sources
                WHERE source_kind=$sourceKind
                  AND provider_account_id=$accountIdentity
                  AND provider_event_id=$eventIdentity;
                """;
            source.Parameters.AddWithValue("$sourceKind", sourceKind);
            source.Parameters.AddWithValue("$accountIdentity", accountIdentity);
            source.Parameters.AddWithValue("$eventIdentity", eventIdentity);
            await using var sourceReader = await source.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await sourceReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                eventId = sourceReader.GetString(0);
                var storedSeriesIdentity = sourceReader.IsDBNull(1)
                    ? null
                    : sourceReader.GetString(1);
                if (!string.Equals(storedSeriesIdentity, seriesIdentity, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "Calendar provider event series provenance is immutable.");
            }
            else
            {
                eventId = null;
            }
        }

        var missingDeletionWasObserved = false;
        if (eventId is null && !input.IsDeleted)
        {
            await using var priorDeletion = connection.CreateCommand();
            priorDeletion.Transaction = transaction;
            priorDeletion.CommandText =
                """
                SELECT EXISTS(
                    SELECT 1 FROM calendar_provider_observations
                    WHERE source_kind=$sourceKind
                      AND provider_account_identity_sha256=$accountIdentity
                      AND provider_event_identity_sha256=$eventIdentity
                      AND outcome='MissingDeletionRetained');
                """;
            priorDeletion.Parameters.AddWithValue("$sourceKind", sourceKind);
            priorDeletion.Parameters.AddWithValue("$accountIdentity", accountIdentity);
            priorDeletion.Parameters.AddWithValue("$eventIdentity", eventIdentity);
            missingDeletionWasObserved = Convert.ToInt32(
                await priorDeletion.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) == 1;
        }

        StoredLocalCalendarEventRevision? storedEvent = null;
        CalendarProviderObservationOutcome outcome;
        var savedUtc = DateTimeOffset.UtcNow;
        if (input.IsDeleted)
        {
            if (eventId is null)
            {
                outcome = CalendarProviderObservationOutcome.MissingDeletionRetained;
            }
            else
            {
                var current = await ReadCalendarEventRevisionAsync(
                    connection, transaction, eventId, revision: null, cancellationToken)
                    .ConfigureAwait(false);
                if (current.IsTombstoned)
                {
                    outcome = CalendarProviderObservationOutcome.AlreadyTombstoned;
                    storedEvent = current;
                }
                else
                {
                    var revision = checked(current.Revision + 1);
                    await InsertCalendarEventRevisionAsync(
                        connection, transaction, eventId, revision, current.Title,
                        current.StartLocal, current.EndLocal, current.AllDay,
                        current.Location, current.Notes, isTombstoned: true,
                        savedUtc, cancellationToken).ConfigureAwait(false);
                    storedEvent = await ReadCalendarEventRevisionAsync(
                        connection, transaction, eventId, revision, cancellationToken)
                        .ConfigureAwait(false);
                    outcome = CalendarProviderObservationOutcome.Tombstoned;
                }
            }
        }
        else
        {
            var title = input.Title.Trim();
            var location = input.Location.Trim();
            if (eventId is null)
            {
                if (missingDeletionWasObserved)
                {
                    outcome = CalendarProviderObservationOutcome.ResurrectionConflictRetained;
                }
                else
                {
                    eventId = Guid.NewGuid().ToString("N");
                    await ExecuteAsync(
                        connection, transaction,
                        "INSERT INTO calendar_events(event_id, created_utc) VALUES ($eventId, $createdUtc);",
                        cancellationToken,
                        ("$eventId", eventId),
                        ("$createdUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture)))
                        .ConfigureAwait(false);
                    await ExecuteAsync(
                        connection, transaction,
                        """
                        INSERT INTO calendar_event_sources(
                            event_id, source_kind, evidence_id, source_message_identity_sha256,
                            provider_account_id, provider_event_id, provider_series_id, created_utc)
                        VALUES ($eventId, $sourceKind, NULL, NULL,
                            $accountIdentity, $eventIdentity, $seriesIdentity, $createdUtc);
                        """,
                        cancellationToken,
                        ("$eventId", eventId), ("$sourceKind", sourceKind),
                        ("$accountIdentity", accountIdentity), ("$eventIdentity", eventIdentity),
                        ("$seriesIdentity", (object?)seriesIdentity ?? DBNull.Value),
                        ("$createdUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture)))
                        .ConfigureAwait(false);
                    await InsertCalendarEventRevisionAsync(
                        connection, transaction, eventId, revision: 1, title,
                        input.StartLocal, input.EndLocal, input.AllDay, location, input.Notes,
                        isTombstoned: false, savedUtc, cancellationToken).ConfigureAwait(false);
                    storedEvent = await ReadCalendarEventRevisionAsync(
                        connection, transaction, eventId, revision: 1, cancellationToken)
                        .ConfigureAwait(false);
                    outcome = CalendarProviderObservationOutcome.Added;
                }
            }
            else
            {
                var current = await ReadCalendarEventRevisionAsync(
                    connection, transaction, eventId, revision: null, cancellationToken)
                    .ConfigureAwait(false);
                if (current.IsTombstoned)
                {
                    storedEvent = current;
                    outcome = CalendarProviderObservationOutcome.ResurrectionConflictRetained;
                }
                else if (CalendarEventContentMatches(current, input))
                {
                    storedEvent = current;
                    outcome = CalendarProviderObservationOutcome.Unchanged;
                }
                else
                {
                    var revision = checked(current.Revision + 1);
                    await InsertCalendarEventRevisionAsync(
                        connection, transaction, eventId, revision, title,
                        input.StartLocal, input.EndLocal, input.AllDay, location, input.Notes,
                        isTombstoned: false, savedUtc, cancellationToken).ConfigureAwait(false);
                    storedEvent = await ReadCalendarEventRevisionAsync(
                        connection, transaction, eventId, revision, cancellationToken)
                        .ConfigureAwait(false);
                    outcome = CalendarProviderObservationOutcome.Updated;
                }
            }
        }

        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO calendar_provider_observations(
                source_kind, provider_account_identity_sha256,
                provider_event_identity_sha256, provider_revision_identity_sha256,
                payload_sha256, event_id, outcome, observed_utc,
                provider_series_identity_sha256)
            VALUES ($sourceKind, $accountIdentity, $eventIdentity, $revisionIdentity,
                $payloadSha256, $eventId, $outcome, $observedUtc, $seriesIdentity);
            """,
            cancellationToken,
            ("$sourceKind", sourceKind), ("$accountIdentity", accountIdentity),
            ("$eventIdentity", eventIdentity), ("$revisionIdentity", revisionIdentity),
            ("$payloadSha256", payloadSha256), ("$eventId", eventId),
            ("$outcome", outcome.ToString()),
            ("$seriesIdentity", (object?)seriesIdentity ?? DBNull.Value),
            ("$observedUtc", input.ObservedUtc.ToString("O", CultureInfo.InvariantCulture)))
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var observation = new StoredCalendarProviderObservation(
            input.SourceKind, accountIdentity, eventIdentity, revisionIdentity,
            payloadSha256, eventId, outcome, input.ObservedUtc, seriesIdentity);
        return new CalendarProviderEventApplyResult(
            observation, storedEvent, WasAlreadyObserved: false);
    }

    public async Task<IReadOnlyList<StoredCalendarProviderObservation>>
        ReadCalendarProviderObservationHistoryAsync(
            CalendarEventSourceKind sourceKind,
            string providerAccountIdentitySha256,
            string providerEventIdentitySha256,
            int offset = 0,
            int maximumCount = 250,
            CancellationToken cancellationToken = default)
    {
        ValidateCalendarProviderIdentity(
            sourceKind, providerAccountIdentitySha256, providerEventIdentitySha256);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 500);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT provider_revision_identity_sha256, payload_sha256, event_id,
                outcome, observed_utc, provider_series_identity_sha256
            FROM calendar_provider_observations
            WHERE source_kind=$sourceKind
              AND provider_account_identity_sha256=$accountIdentity
              AND provider_event_identity_sha256=$eventIdentity
            ORDER BY rowid
            LIMIT $maximumCount OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$sourceKind", sourceKind.ToString());
        command.Parameters.AddWithValue("$accountIdentity", providerAccountIdentitySha256);
        command.Parameters.AddWithValue("$eventIdentity", providerEventIdentitySha256);
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        command.Parameters.AddWithValue("$offset", offset);
        var observations = new List<StoredCalendarProviderObservation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Enum.TryParse<CalendarProviderObservationOutcome>(
                    reader.GetString(3), ignoreCase: false, out var outcome))
                throw new InvalidDataException("A stored calendar provider outcome is invalid.");
            observations.Add(new StoredCalendarProviderObservation(
                sourceKind,
                providerAccountIdentitySha256,
                providerEventIdentitySha256,
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                outcome,
                DateTimeOffset.Parse(
                    reader.GetString(4), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return observations;
    }

    public async Task<StoredCalendarProviderSeriesSnapshot?>
        ReadLatestCalendarProviderSeriesSnapshotAsync(
            CalendarEventSourceKind sourceKind,
            string providerAccountIdentitySha256,
            string providerSeriesIdentitySha256,
            CancellationToken cancellationToken = default)
    {
        ValidateCalendarProviderIdentity(
            sourceKind, providerAccountIdentitySha256, providerSeriesIdentitySha256);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLatestCalendarProviderSeriesSnapshotAsync(
            connection, transaction: null, sourceKind, providerAccountIdentitySha256,
            providerSeriesIdentitySha256, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CalendarProviderSeriesSaveResult>
        SaveCalendarProviderSeriesSnapshotAsync(
            CalendarProviderSeriesSnapshotImport input,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var members = ValidateAndNormalizeCalendarProviderSeriesSnapshot(input);
        var payloadSha256 = HashCalendarProviderSeriesSnapshot(input, members);
        var sourceKind = input.SourceKind.ToString();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var repeated = connection.CreateCommand())
        {
            repeated.Transaction = transaction;
            repeated.CommandText =
                """
                SELECT payload_sha256, observed_utc
                FROM calendar_provider_series_snapshots
                WHERE source_kind=$sourceKind
                  AND provider_account_identity_sha256=$accountIdentity
                  AND provider_series_identity_sha256=$seriesIdentity
                  AND provider_series_revision_identity_sha256=$revisionIdentity;
                """;
            repeated.Parameters.AddWithValue("$sourceKind", sourceKind);
            repeated.Parameters.AddWithValue(
                "$accountIdentity", input.ProviderAccountIdentitySha256);
            repeated.Parameters.AddWithValue(
                "$seriesIdentity", input.ProviderSeriesIdentitySha256);
            repeated.Parameters.AddWithValue(
                "$revisionIdentity", input.ProviderSeriesRevisionIdentitySha256);
            await using var reader = await repeated.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(0), payloadSha256, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "A provider series revision identity was reused with different occurrence membership.");
                var observedUtc = DateTimeOffset.Parse(
                    reader.GetString(1), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
                await reader.DisposeAsync().ConfigureAwait(false);
                var storedMembers = await ReadCalendarProviderSeriesMembersAsync(
                    connection, transaction, input.SourceKind,
                    input.ProviderAccountIdentitySha256,
                    input.ProviderSeriesIdentitySha256,
                    input.ProviderSeriesRevisionIdentitySha256,
                    cancellationToken).ConfigureAwait(false);
                if (!storedMembers.SequenceEqual(members, StringComparer.Ordinal))
                    throw new InvalidDataException(
                        "Stored provider series membership differs from its immutable payload.");
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new CalendarProviderSeriesSaveResult(
                    new StoredCalendarProviderSeriesSnapshot(
                        input.SourceKind,
                        input.ProviderAccountIdentitySha256,
                        input.ProviderSeriesIdentitySha256,
                        input.ProviderSeriesRevisionIdentitySha256,
                        payloadSha256,
                        storedMembers,
                        observedUtc),
                    WasAlreadyPresent: true);
            }
        }

        var latest = await ReadLatestCalendarProviderSeriesSnapshotAsync(
            connection, transaction, input.SourceKind,
            input.ProviderAccountIdentitySha256,
            input.ProviderSeriesIdentitySha256,
            cancellationToken).ConfigureAwait(false);
        if (latest is not null && input.ObservedUtc <= latest.ObservedUtc)
            throw new InvalidDataException(
                "A new provider series snapshot must be observed after the latest retained snapshot.");

        foreach (var member in members)
        {
            await using var bound = connection.CreateCommand();
            bound.Transaction = transaction;
            bound.CommandText =
                """
                SELECT COUNT(*) FROM calendar_event_sources
                WHERE source_kind=$sourceKind
                  AND provider_account_id=$accountIdentity
                  AND provider_event_id=$eventIdentity
                  AND provider_series_id=$seriesIdentity;
                """;
            bound.Parameters.AddWithValue("$sourceKind", sourceKind);
            bound.Parameters.AddWithValue(
                "$accountIdentity", input.ProviderAccountIdentitySha256);
            bound.Parameters.AddWithValue("$eventIdentity", member);
            bound.Parameters.AddWithValue(
                "$seriesIdentity", input.ProviderSeriesIdentitySha256);
            if (Convert.ToInt64(
                    await bound.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidDataException(
                    "Provider series membership references an occurrence without matching immutable provenance.");
            }
        }

        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO calendar_provider_series_snapshots(
                source_kind, provider_account_identity_sha256,
                provider_series_identity_sha256,
                provider_series_revision_identity_sha256,
                payload_sha256, observed_utc)
            VALUES ($sourceKind, $accountIdentity, $seriesIdentity,
                $revisionIdentity, $payloadSha256, $observedUtc);
            """,
            cancellationToken,
            ("$sourceKind", sourceKind),
            ("$accountIdentity", input.ProviderAccountIdentitySha256),
            ("$seriesIdentity", input.ProviderSeriesIdentitySha256),
            ("$revisionIdentity", input.ProviderSeriesRevisionIdentitySha256),
            ("$payloadSha256", payloadSha256),
            ("$observedUtc", input.ObservedUtc.ToString("O", CultureInfo.InvariantCulture)))
            .ConfigureAwait(false);
        for (var ordinal = 0; ordinal < members.Count; ordinal++)
        {
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO calendar_provider_series_snapshot_members(
                    source_kind, provider_account_identity_sha256,
                    provider_series_identity_sha256,
                    provider_series_revision_identity_sha256,
                    ordinal, provider_event_identity_sha256)
                VALUES ($sourceKind, $accountIdentity, $seriesIdentity,
                    $revisionIdentity, $ordinal, $eventIdentity);
                """,
                cancellationToken,
                ("$sourceKind", sourceKind),
                ("$accountIdentity", input.ProviderAccountIdentitySha256),
                ("$seriesIdentity", input.ProviderSeriesIdentitySha256),
                ("$revisionIdentity", input.ProviderSeriesRevisionIdentitySha256),
                ("$ordinal", ordinal),
                ("$eventIdentity", members[ordinal])).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CalendarProviderSeriesSaveResult(
            new StoredCalendarProviderSeriesSnapshot(
                input.SourceKind,
                input.ProviderAccountIdentitySha256,
                input.ProviderSeriesIdentitySha256,
                input.ProviderSeriesRevisionIdentitySha256,
                payloadSha256,
                members,
                input.ObservedUtc),
            WasAlreadyPresent: false);
    }

    private static async Task<StoredCalendarProviderSeriesSnapshot?>
        ReadLatestCalendarProviderSeriesSnapshotAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            CalendarEventSourceKind sourceKind,
            string accountIdentity,
            string seriesIdentity,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT provider_series_revision_identity_sha256, payload_sha256,
                observed_utc
            FROM calendar_provider_series_snapshots
            WHERE source_kind=$sourceKind
              AND provider_account_identity_sha256=$accountIdentity
              AND provider_series_identity_sha256=$seriesIdentity
            ORDER BY rowid DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sourceKind", sourceKind.ToString());
        command.Parameters.AddWithValue("$accountIdentity", accountIdentity);
        command.Parameters.AddWithValue("$seriesIdentity", seriesIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var revisionIdentity = reader.GetString(0);
        var payloadSha256 = reader.GetString(1);
        var observedUtc = DateTimeOffset.Parse(
            reader.GetString(2), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        await reader.DisposeAsync().ConfigureAwait(false);
        var members = await ReadCalendarProviderSeriesMembersAsync(
            connection, transaction, sourceKind, accountIdentity, seriesIdentity,
            revisionIdentity, cancellationToken).ConfigureAwait(false);
        return new StoredCalendarProviderSeriesSnapshot(
            sourceKind, accountIdentity, seriesIdentity, revisionIdentity,
            payloadSha256, members, observedUtc);
    }

    private static async Task<IReadOnlyList<string>> ReadCalendarProviderSeriesMembersAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CalendarEventSourceKind sourceKind,
        string accountIdentity,
        string seriesIdentity,
        string revisionIdentity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT provider_event_identity_sha256
            FROM calendar_provider_series_snapshot_members
            WHERE source_kind=$sourceKind
              AND provider_account_identity_sha256=$accountIdentity
              AND provider_series_identity_sha256=$seriesIdentity
              AND provider_series_revision_identity_sha256=$revisionIdentity
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$sourceKind", sourceKind.ToString());
        command.Parameters.AddWithValue("$accountIdentity", accountIdentity);
        command.Parameters.AddWithValue("$seriesIdentity", seriesIdentity);
        command.Parameters.AddWithValue("$revisionIdentity", revisionIdentity);
        var members = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            members.Add(reader.GetString(0));
            if (members.Count > 500)
                throw new InvalidDataException(
                    "Stored calendar provider series membership exceeds its safety limit.");
        }
        return members;
    }

    public async Task<IReadOnlyList<StoredLocalCalendarEventRevision>> ReadLocalCalendarAgendaAsync(
        DateTime rangeStartLocal,
        DateTime rangeEndLocal,
        int maximumCount = 250,
        CancellationToken cancellationToken = default)
    {
        ValidateLocalCalendarRange(rangeStartLocal, rangeEndLocal);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 500);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH latest AS (
                SELECT event_id, MAX(revision) AS revision
                FROM calendar_event_revisions GROUP BY event_id)
            SELECT r.event_id, r.revision, r.title, r.start_local, r.end_local,
                r.all_day, r.location, r.notes, r.is_tombstoned, r.saved_utc,
                s.source_kind, s.evidence_id, s.source_message_identity_sha256,
                s.provider_account_id, s.provider_event_id, s.provider_series_id
            FROM latest
            JOIN calendar_event_revisions r
              ON r.event_id=latest.event_id AND r.revision=latest.revision
            JOIN calendar_event_sources s ON s.event_id=r.event_id
            WHERE r.is_tombstoned=0
              AND r.start_local < $rangeEndLocal
              AND r.end_local > $rangeStartLocal
            ORDER BY r.start_local, r.end_local, r.title COLLATE NOCASE, r.event_id
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue(
            "$rangeStartLocal", FormatCalendarLocalTimestamp(rangeStartLocal));
        command.Parameters.AddWithValue(
            "$rangeEndLocal", FormatCalendarLocalTimestamp(rangeEndLocal));
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        return await ReadCalendarEventRowsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredLocalCalendarEventRevision>>
        ReadLocalCalendarEventRevisionHistoryAsync(
            string eventId,
            CancellationToken cancellationToken = default)
    {
        var normalizedEventId = NormalizeCalendarEventId(eventId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.event_id, r.revision, r.title, r.start_local, r.end_local,
                r.all_day, r.location, r.notes, r.is_tombstoned, r.saved_utc,
                s.source_kind, s.evidence_id, s.source_message_identity_sha256,
                s.provider_account_id, s.provider_event_id, s.provider_series_id
            FROM calendar_event_revisions r
            JOIN calendar_event_sources s ON s.event_id=r.event_id
            WHERE r.event_id=$eventId
            ORDER BY r.revision;
            """;
        command.Parameters.AddWithValue("$eventId", normalizedEventId);
        return await ReadCalendarEventRowsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredContactRevision> SaveContactRevisionAsync(
        ContactRevisionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateContactRevisionInput(input);
        var contactId = string.IsNullOrWhiteSpace(input.ContactId)
            ? Guid.NewGuid().ToString("N")
            : NormalizeContactId(input.ContactId);
        var savedUtc = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        int revision;
        if (string.IsNullOrWhiteSpace(input.ContactId))
        {
            await ExecuteAsync(
                connection, transaction,
                "INSERT INTO contacts(contact_id, created_utc) VALUES ($contactId, $createdUtc);",
                cancellationToken,
                ("$contactId", contactId),
                ("$createdUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            revision = 1;
        }
        else
        {
            await using var latest = connection.CreateCommand();
            latest.Transaction = transaction;
            latest.CommandText =
                "SELECT revision, is_tombstoned FROM contact_revisions " +
                "WHERE contact_id=$contactId ORDER BY revision DESC LIMIT 1;";
            latest.Parameters.AddWithValue("$contactId", contactId);
            await using var reader = await latest.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new KeyNotFoundException("Contact was not found.");
            if (reader.GetInt64(1) != 0)
                throw new InvalidOperationException("A tombstoned contact cannot be edited or resurrected.");
            revision = checked(reader.GetInt32(0) + 1);
        }
        await InsertContactRevisionAsync(
            connection, transaction, contactId, revision, input.DisplayName.Trim(),
            input.EmailAddress.Trim(), input.Notes, input.IsGroup, isTombstoned: false,
            input.GroupMembers ?? [], savedUtc, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadContactRevisionAsync(contactId, revision, cancellationToken).ConfigureAwait(false);
    }

    public async Task TombstoneContactAsync(
        string contactId,
        CancellationToken cancellationToken = default)
    {
        var normalizedContactId = NormalizeContactId(contactId);
        var savedUtc = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var latest = connection.CreateCommand();
        latest.Transaction = transaction;
        latest.CommandText =
            "SELECT revision, display_name, email_address, notes, is_group, is_tombstoned " +
            "FROM contact_revisions WHERE contact_id=$contactId ORDER BY revision DESC LIMIT 1;";
        latest.Parameters.AddWithValue("$contactId", normalizedContactId);
        int priorRevision;
        string displayName;
        string emailAddress;
        string notes;
        bool isGroup;
        await using (var reader = await latest.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new KeyNotFoundException("Contact was not found.");
            priorRevision = reader.GetInt32(0);
            displayName = reader.GetString(1);
            emailAddress = reader.GetString(2);
            notes = reader.GetString(3);
            isGroup = reader.GetInt64(4) != 0;
            if (reader.GetInt64(5) != 0) return;
        }
        var nextRevision = checked(priorRevision + 1);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO contact_revisions(
                contact_id, revision, display_name, email_address, notes,
                is_group, is_tombstoned, saved_utc)
            VALUES ($contactId, $revision, $displayName, $emailAddress, $notes,
                $isGroup, 1, $savedUtc);
            """,
            cancellationToken,
            ("$contactId", normalizedContactId), ("$revision", nextRevision),
            ("$displayName", displayName), ("$emailAddress", emailAddress), ("$notes", notes),
            ("$isGroup", isGroup ? 1 : 0),
            ("$savedUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO contact_revision_group_members(contact_id, revision, ordinal, member_kind, member_value)
            SELECT contact_id, $nextRevision, ordinal, member_kind, member_value
            FROM contact_revision_group_members
            WHERE contact_id=$contactId AND revision=$priorRevision;
            """,
            cancellationToken,
            ("$nextRevision", nextRevision), ("$contactId", normalizedContactId),
            ("$priorRevision", priorRevision)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredContactRevision>> ReadLatestContactsAsync(
        string? searchText = null,
        int maximumCount = 250,
        long offset = 0,
        bool includeTombstoned = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 500);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var search = (searchText ?? string.Empty).Trim();
        if (search.Length > 512 || search.Any(character => character is '\r' or '\n' or '\0'))
            throw new InvalidDataException("Contact search text is invalid or exceeds its limit.");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH latest AS (
                SELECT contact_id, MAX(revision) AS revision
                FROM contact_revisions GROUP BY contact_id)
            SELECT r.contact_id, r.revision, r.display_name, r.email_address, r.notes,
                r.is_group, r.is_tombstoned, r.saved_utc
            FROM latest
            JOIN contact_revisions r ON r.contact_id=latest.contact_id AND r.revision=latest.revision
            WHERE ($includeTombstoned=1 OR r.is_tombstoned=0)
              AND ($search='' OR r.display_name LIKE $pattern ESCAPE '\'
                  OR r.email_address LIKE $pattern ESCAPE '\'
                  OR r.notes LIKE $pattern ESCAPE '\'
                  OR EXISTS (
                      SELECT 1 FROM contact_revision_group_members gm
                      WHERE gm.contact_id=r.contact_id AND gm.revision=r.revision
                        AND gm.member_value LIKE $pattern ESCAPE '\'))
            ORDER BY r.display_name COLLATE NOCASE, r.email_address COLLATE NOCASE, r.contact_id
            LIMIT $maximumCount OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$includeTombstoned", includeTombstoned ? 1 : 0);
        command.Parameters.AddWithValue("$search", search);
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLikePattern(search)}%");
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        command.Parameters.AddWithValue("$offset", offset);
        var rows = await ReadContactBaseRowsAsync(command, cancellationToken).ConfigureAwait(false);
        return await MaterializeContactRowsAsync(connection, rows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredContactRevision>> ReadContactRevisionHistoryAsync(
        string contactId,
        CancellationToken cancellationToken = default)
    {
        var normalizedContactId = NormalizeContactId(contactId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.contact_id, r.revision, r.display_name, r.email_address, r.notes,
                r.is_group, r.is_tombstoned, r.saved_utc
            FROM contact_revisions r
            WHERE r.contact_id=$contactId
            ORDER BY r.revision;
            """;
        command.Parameters.AddWithValue("$contactId", normalizedContactId);
        var rows = await ReadContactBaseRowsAsync(command, cancellationToken).ConfigureAwait(false);
        return await MaterializeContactRowsAsync(connection, rows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredContactRevision>> ReadActiveContactsForRecipientResolutionAsync(
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 500;
        const int maximumContacts = 100_000;
        var contacts = new List<StoredContactRevision>();
        long offset = 0;
        while (true)
        {
            var page = await ReadLatestContactsAsync(
                searchText: null, maximumCount: pageSize, offset: offset,
                includeTombstoned: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (contacts.Count + page.Count > maximumContacts)
                throw new InvalidDataException("Active contact count exceeds the recipient-resolution limit.");
            contacts.AddRange(page);
            if (page.Count < pageSize) break;
            offset = checked(offset + page.Count);
            if (contacts.Count == maximumContacts)
            {
                var overflow = await ReadLatestContactsAsync(
                    searchText: null, maximumCount: 1, offset: offset,
                    includeTombstoned: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (overflow.Count != 0)
                    throw new InvalidDataException("Active contact count exceeds the recipient-resolution limit.");
                break;
            }
        }
        return contacts;
    }

    public Task<long> GetContactCountAsync(CancellationToken cancellationToken = default) =>
        GetScalarCountAsync("SELECT COUNT(*) FROM contacts;", cancellationToken);

    public async Task<byte[]> ReadStoredMimeAsync(string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT b.relative_path, m.raw_mime_sha256, m.raw_mime_bytes FROM messages m
            JOIN content_blobs b ON b.sha256 = m.raw_mime_sha256
            WHERE m.message_id = $messageId;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new KeyNotFoundException("Stored message was not found.");
        return await ReadVerifiedContentAsync(
            reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            "Stored MIME", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredAttachmentSummary>> ReadStoredAttachmentSummariesAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT message_id, ordinal, file_name, media_type, byte_count, content_sha256, content_id
            FROM attachments
            WHERE message_id = $messageId
            ORDER BY ordinal
            LIMIT 2049;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var attachments = new List<StoredAttachmentSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attachments.Add(new StoredAttachmentSummary(
                reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetString(6)));
            if (attachments.Count > MaximumAttachmentsPerMessage)
                throw new InvalidDataException("Stored message exceeds the attachment-count safety limit.");
        }
        return attachments;
    }

    public async Task<byte[]> ReadStoredAttachmentContentAsync(
        string messageId,
        int ordinal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT b.relative_path, a.content_sha256, a.byte_count
            FROM attachments a
            JOIN content_blobs b ON b.sha256 = a.content_sha256
            WHERE a.message_id = $messageId AND a.ordinal = $ordinal;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new KeyNotFoundException("Stored attachment was not found.");
        return await ReadVerifiedContentAsync(
            reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            "Stored attachment", cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredMessagePage> ListStoredMessagesPageAsync(
        int pageSize,
        long offset = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateStoredMessagePage(pageSize, offset);
        return await ReadStoredMessagePageAsync(
            """
            SELECT m.message_id, fm.folder_id, m.subject, b.sender, b.recipients,
                   b.sent_utc, substr(COALESCE(b.text_body, ''), 1, 512),
                   m.raw_mime_sha256, m.raw_mime_bytes,
                   COALESCE((SELECT group_concat(ff.flag, char(31))
                             FROM folder_message_flags ff
                             WHERE ff.folder_id = fm.folder_id AND ff.message_id = fm.message_id), '')
            FROM messages m
            JOIN normalized_message_bodies b ON b.message_id = m.message_id
            JOIN folder_messages fm ON fm.message_id = m.message_id
            WHERE m.is_tombstoned = 0 AND fm.is_tombstoned = 0
            ORDER BY m.imported_utc DESC, m.message_id, fm.folder_id
            LIMIT $fetchCount OFFSET $offset;
            """,
            pageSize, offset, match: null, folderId: null, accountId: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredMessagePage> ListStoredAccountMessagesPageAsync(
        string accountId,
        int pageSize,
        long offset = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(accountId, 64, nameof(accountId));
        ValidateStoredMessagePage(pageSize, offset);
        return await ReadStoredMessagePageAsync(
            """
            SELECT m.message_id, fm.folder_id, m.subject, b.sender, b.recipients,
                   b.sent_utc, substr(COALESCE(b.text_body, ''), 1, 512),
                   m.raw_mime_sha256, m.raw_mime_bytes,
                   COALESCE((SELECT group_concat(ff.flag, char(31))
                             FROM folder_message_flags ff
                             WHERE ff.folder_id = fm.folder_id AND ff.message_id = fm.message_id), '')
            FROM messages m
            JOIN normalized_message_bodies b ON b.message_id = m.message_id
            JOIN folder_messages fm ON fm.message_id = m.message_id
            WHERE m.account_id = $accountId
              AND m.is_tombstoned = 0 AND fm.is_tombstoned = 0
            ORDER BY m.imported_utc DESC, m.message_id, fm.folder_id
            LIMIT $fetchCount OFFSET $offset;
            """,
            pageSize, offset, match: null, folderId: null, accountId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredMessagePage> ListStoredFolderMessagesPageAsync(
        string folderId,
        int pageSize,
        long offset = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateStoredFolderId(folderId);
        ValidateStoredMessagePage(pageSize, offset);
        return await ReadStoredMessagePageAsync(
            """
            SELECT m.message_id, fm.folder_id, m.subject, b.sender, b.recipients,
                   b.sent_utc, substr(COALESCE(b.text_body, ''), 1, 512),
                   m.raw_mime_sha256, m.raw_mime_bytes,
                   COALESCE((SELECT group_concat(ff.flag, char(31))
                             FROM folder_message_flags ff
                             WHERE ff.folder_id = fm.folder_id AND ff.message_id = fm.message_id), '')
            FROM messages m
            JOIN normalized_message_bodies b ON b.message_id = m.message_id
            JOIN folder_messages fm ON fm.message_id = m.message_id
            WHERE fm.folder_id = $folderId
              AND m.is_tombstoned = 0
              AND fm.is_tombstoned = 0
            ORDER BY m.imported_utc DESC, m.message_id
            LIMIT $fetchCount OFFSET $offset;
            """,
            pageSize, offset, match: null, folderId: folderId, accountId: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredMessagePage> SearchStoredMessagesPageAsync(
        string query,
        int pageSize,
        long offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ValidateStoredMessagePage(pageSize, offset);
        var match = BuildFtsQuery(query);
        if (match.Length == 0) return new StoredMessagePage([], null);
        return await ReadStoredMessagePageAsync(
            """
            SELECT m.message_id, fm.folder_id, m.subject, b.sender, b.recipients,
                   b.sent_utc, snippet(message_search, 4, '', '', '…', 18),
                   m.raw_mime_sha256, m.raw_mime_bytes,
                   COALESCE((SELECT group_concat(ff.flag, char(31))
                             FROM folder_message_flags ff
                             WHERE ff.folder_id = fm.folder_id AND ff.message_id = fm.message_id), '')
            FROM message_search
            JOIN messages m ON m.message_id = message_search.message_id
            JOIN normalized_message_bodies b ON b.message_id = m.message_id
            JOIN folder_messages fm ON fm.message_id = m.message_id
            WHERE message_search MATCH $match
              AND m.is_tombstoned = 0
              AND fm.is_tombstoned = 0
            ORDER BY bm25(message_search), m.imported_utc DESC, m.message_id, fm.folder_id
            LIMIT $fetchCount OFFSET $offset;
            """,
            pageSize, offset, match, folderId: null, accountId: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredMessagePage> SearchStoredAccountMessagesPageAsync(
        string accountId,
        string query,
        int pageSize,
        long offset = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(accountId, 64, nameof(accountId));
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ValidateStoredMessagePage(pageSize, offset);
        var match = BuildFtsQuery(query);
        if (match.Length == 0) return new StoredMessagePage([], null);
        return await ReadStoredMessagePageAsync(
            """
            SELECT m.message_id, fm.folder_id, m.subject, b.sender, b.recipients,
                   b.sent_utc, snippet(message_search, 4, '', '', '…', 18),
                   m.raw_mime_sha256, m.raw_mime_bytes,
                   COALESCE((SELECT group_concat(ff.flag, char(31))
                             FROM folder_message_flags ff
                             WHERE ff.folder_id = fm.folder_id AND ff.message_id = fm.message_id), '')
            FROM message_search
            JOIN messages m ON m.message_id = message_search.message_id
            JOIN normalized_message_bodies b ON b.message_id = m.message_id
            JOIN folder_messages fm ON fm.message_id = m.message_id
            WHERE message_search MATCH $match
              AND m.account_id = $accountId
              AND m.is_tombstoned = 0 AND fm.is_tombstoned = 0
            ORDER BY bm25(message_search), m.imported_utc DESC, m.message_id, fm.folder_id
            LIMIT $fetchCount OFFSET $offset;
            """,
            pageSize, offset, match, folderId: null, accountId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredMessagePage> SearchStoredFolderMessagesPageAsync(
        string folderId,
        string query,
        int pageSize,
        long offset = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateStoredFolderId(folderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ValidateStoredMessagePage(pageSize, offset);
        var match = BuildFtsQuery(query);
        if (match.Length == 0) return new StoredMessagePage([], null);
        return await ReadStoredMessagePageAsync(
            """
            SELECT m.message_id, fm.folder_id, m.subject, b.sender, b.recipients,
                   b.sent_utc, snippet(message_search, 4, '', '', '…', 18),
                   m.raw_mime_sha256, m.raw_mime_bytes,
                   COALESCE((SELECT group_concat(ff.flag, char(31))
                             FROM folder_message_flags ff
                             WHERE ff.folder_id = fm.folder_id AND ff.message_id = fm.message_id), '')
            FROM message_search
            JOIN messages m ON m.message_id = message_search.message_id
            JOIN normalized_message_bodies b ON b.message_id = m.message_id
            JOIN folder_messages fm ON fm.message_id = m.message_id
            WHERE message_search MATCH $match
              AND fm.folder_id = $folderId
              AND m.is_tombstoned = 0
              AND fm.is_tombstoned = 0
            ORDER BY bm25(message_search), m.imported_utc DESC, m.message_id
            LIMIT $fetchCount OFFSET $offset;
            """,
            pageSize, offset, match, folderId, accountId: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredMessageSearchResult>> SearchMessagesAsync(
        string query,
        int maximumResults = 100,
        CancellationToken cancellationToken = default)
    {
        var page = await SearchStoredMessagesPageAsync(
            query, maximumResults, offset: 0, cancellationToken: cancellationToken).ConfigureAwait(false);
        return page.Messages;
    }

    private async Task<StoredMessagePage> ReadStoredMessagePageAsync(
        string sql,
        int pageSize,
        long offset,
        string? match,
        string? folderId,
        string? accountId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$fetchCount", checked(pageSize + 1));
        command.Parameters.AddWithValue("$offset", offset);
        if (match is not null) command.Parameters.AddWithValue("$match", match);
        if (folderId is not null) command.Parameters.AddWithValue("$folderId", folderId);
        if (accountId is not null) command.Parameters.AddWithValue("$accountId", accountId);
        var results = new List<StoredMessageSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset? sentUtc = null;
            if (!reader.IsDBNull(5) &&
                DateTimeOffset.TryParse(
                    reader.GetString(5), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsedSentUtc)) sentUtc = parsedSentUtc;
            results.Add(new StoredMessageSearchResult(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), sentUtc,
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.GetString(7), reader.GetInt64(8),
                reader.IsDBNull(9) || string.IsNullOrEmpty(reader.GetString(9))
                    ? []
                    : reader.GetString(9).Split((char)31, StringSplitOptions.RemoveEmptyEntries)));
        }
        var hasMore = results.Count > pageSize;
        if (hasMore) results.RemoveAt(results.Count - 1);
        return new StoredMessagePage(
            results, hasMore ? checked(offset + results.Count) : null);
    }

    public async Task<IReadOnlyList<StoredFolderSummary>> ReadStoredFolderSummariesAsync(
        int maximumCount = 1_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 1_000);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.account_id, f.folder_id, f.parent_folder_id, f.display_name,
                   COUNT(CASE WHEN fm.message_id IS NOT NULL
                                   AND fm.is_tombstoned = 0
                                   AND m.is_tombstoned = 0 THEN 1 END),
                   COUNT(CASE WHEN fm.message_id IS NOT NULL
                                   AND (fm.is_tombstoned != 0 OR m.is_tombstoned != 0) THEN 1 END),
                   f.is_sent_items, f.is_deleted_items, f.is_tombstoned, f.is_local_only, f.remote_full_name,
                   EXISTS(SELECT 1 FROM local_folder_revisions r WHERE r.folder_id=f.folder_id)
            FROM folders f
            LEFT JOIN folder_messages fm ON fm.folder_id = f.folder_id
            LEFT JOIN messages m ON m.message_id = fm.message_id
            GROUP BY f.account_id, f.folder_id, f.parent_folder_id, f.display_name,
                     f.is_sent_items, f.is_deleted_items, f.is_tombstoned, f.is_local_only, f.remote_full_name
            ORDER BY f.account_id, f.parent_folder_id IS NOT NULL,
                     f.display_name COLLATE NOCASE, f.folder_id
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        var folders = new List<StoredFolderSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            folders.Add(new StoredFolderSummary(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6) != 0,
                reader.GetInt64(7) != 0, reader.GetInt64(8) != 0, reader.GetInt64(9) != 0,
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetInt64(11) != 0));
        }
        return folders;
    }

    public async Task<long> GetMessageCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    public Task<long> GetFolderCountAsync(CancellationToken cancellationToken = default) =>
        GetScalarCountAsync("SELECT COUNT(*) FROM folders;", cancellationToken);

    public Task<long> GetFolderMessageLinkCountAsync(CancellationToken cancellationToken = default) =>
        GetScalarCountAsync("SELECT COUNT(*) FROM folder_messages;", cancellationToken);

    public Task<long> GetAttachmentCountAsync(CancellationToken cancellationToken = default) =>
        GetScalarCountAsync("SELECT COUNT(*) FROM attachments;", cancellationToken);

    /// <summary>
    /// Measures the local normalized store without changing it.  Physical
    /// compaction is intentionally unavailable because the data-preservation
    /// contract retains tombstones, message bytes, and immutable revisions.
    /// </summary>
    public async Task<StoredNoPurgeCompactionAssessment> ReadNoPurgeCompactionAssessmentAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DatabasePath))
            throw new FileNotFoundException("The normalized modern store does not exist.", DatabasePath);

        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        var dataVersionBefore = await ReadSqliteDataVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        var databaseBytesBefore = GetSqliteDurableByteCount();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM content_blobs),
                COALESCE((SELECT SUM(byte_count) FROM content_blobs), 0),
                (SELECT COUNT(*) FROM messages),
                (SELECT COUNT(*) FROM messages WHERE is_tombstoned != 0),
                (SELECT COUNT(*) FROM folder_messages),
                (SELECT COUNT(*) FROM folder_messages WHERE is_tombstoned != 0),
                (SELECT COUNT(*) FROM folders),
                (SELECT COUNT(*) FROM folders WHERE is_tombstoned != 0),
                (SELECT COUNT(*) FROM attachments),
                COALESCE((SELECT SUM(byte_count) FROM attachments), 0),
                (SELECT
                    (SELECT COUNT(*) FROM local_folder_revisions) +
                    (SELECT COUNT(*) FROM local_folder_hierarchy_revisions) +
                    (SELECT COUNT(*) FROM calendar_event_sources) +
                    (SELECT COUNT(*) FROM calendar_event_revisions) +
                    (SELECT COUNT(*) FROM calendar_provider_observations) +
                    (SELECT COUNT(*) FROM calendar_provider_series_snapshots) +
                    (SELECT COUNT(*) FROM calendar_provider_series_snapshot_members) +
                    (SELECT COUNT(*) FROM contact_revisions) +
                    (SELECT COUNT(*) FROM draft_revisions) +
                    (SELECT COUNT(*) FROM operation_journal)),
                (SELECT COUNT(*) FROM pending_operations);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The normalized modern store did not return compaction accounting.");

        var assessment = new StoredNoPurgeCompactionAssessment(
            databaseBytesBefore,
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
            reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11),
            ReclaimableBytes: 0,
            PhysicalPurgePermitted: false);
        await reader.DisposeAsync().ConfigureAwait(false);
        var dataVersionAfter = await ReadSqliteDataVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        var databaseBytesAfter = GetSqliteDurableByteCount();
        if (dataVersionBefore != dataVersionAfter || databaseBytesBefore != databaseBytesAfter)
        {
            throw new IOException(
                "The normalized modern store changed while its no-purge assessment was running; no result is trusted.");
        }
        return assessment;
    }

    public async Task EnsureImapFolderAsync(
        string accountId,
        string folderId,
        string displayName,
        string remoteFullName,
        CancellationToken cancellationToken = default)
    {
        await EnsureImapFolderAsync(
            accountId,
            folderId,
            displayName,
            remoteFullName,
            designateAsDeletedItems: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureImapFolderAsync(
        string accountId,
        string folderId,
        string displayName,
        string remoteFullName,
        bool designateAsDeletedItems,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(accountId, 64, nameof(accountId));
        ValidateMailboxMutationIdentifier(folderId, 512, nameof(folderId));
        ValidateBoundedProfileText(displayName, 512, nameof(displayName));
        ValidateMailboxMutationIdentifier(remoteFullName, 512, nameof(remoteFullName));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO accounts(account_id) VALUES ($accountId);",
            cancellationToken, ("$accountId", accountId)).ConfigureAwait(false);
        if (designateAsDeletedItems)
        {
            await using var existingDeleted = connection.CreateCommand();
            existingDeleted.Transaction = transaction;
            existingDeleted.CommandText =
                "SELECT folder_id FROM folders " +
                "WHERE account_id=$accountId AND is_deleted_items=1 AND is_tombstoned=0 LIMIT 1;";
            existingDeleted.Parameters.AddWithValue("$accountId", accountId);
            var existingDeletedFolder = await existingDeleted.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) as string;
            if (existingDeletedFolder is not null &&
                !existingDeletedFolder.Equals(folderId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "This account already has a designated Deleted Items folder.");
            }
        }
        var inserted = await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO folders(
                folder_id, account_id, display_name, is_deleted_items,
                is_local_only, remote_full_name)
            VALUES (
                $folderId, $accountId, $displayName, $isDeletedItems,
                0, $remoteFullName)
            ON CONFLICT(folder_id) DO NOTHING;
            """,
            cancellationToken,
            ("$folderId", folderId),
            ("$accountId", accountId),
            ("$displayName", displayName),
            ("$isDeletedItems", designateAsDeletedItems ? 1 : 0),
            ("$remoteFullName", remoteFullName)).ConfigureAwait(false);
        if (inserted == 0)
        {
            await using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText =
                """
                SELECT account_id, display_name, is_deleted_items, is_local_only, remote_full_name
                FROM folders WHERE folder_id=$folderId AND is_tombstoned=0;
                """;
            existing.Parameters.AddWithValue("$folderId", folderId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException(
                    "IMAP folder binding conflicts with an existing local or remote folder definition.");
            var existingAccount = reader.GetString(0);
            var existingDisplayName = reader.GetString(1);
            var existingIsDeletedItems = reader.GetInt64(2) != 0;
            var existingIsLocalOnly = reader.GetInt64(3) != 0;
            var existingRemoteFullName = reader.IsDBNull(4) ? null : reader.GetString(4);
            await reader.DisposeAsync().ConfigureAwait(false);
            var exactBinding = existingAccount.Equals(accountId, StringComparison.Ordinal) &&
                existingDisplayName.Equals(displayName, StringComparison.Ordinal) &&
                !existingIsLocalOnly &&
                existingRemoteFullName?.Equals(remoteFullName, StringComparison.Ordinal) is true;
            if (exactBinding)
            {
                if (designateAsDeletedItems && !existingIsDeletedItems)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        UPDATE folders SET is_deleted_items=1
                        WHERE folder_id=$folderId AND account_id=$accountId
                          AND is_tombstoned=0 AND is_deleted_items=0;
                        """,
                        cancellationToken,
                        ("$folderId", folderId),
                        ("$accountId", accountId)).ConfigureAwait(false);
                }
            }
            else
            {
                var promotablePriorImapFolder = existingAccount.Equals(accountId, StringComparison.Ordinal) &&
                    existingIsLocalOnly && existingRemoteFullName is null &&
                    !folderId.StartsWith("local-", StringComparison.Ordinal);
                if (promotablePriorImapFolder)
                {
                    await using var cursor = connection.CreateCommand();
                    cursor.Transaction = transaction;
                    cursor.CommandText =
                        "SELECT 1 FROM sync_cursors WHERE account_id=$accountId AND folder_id=$folderId LIMIT 1;";
                    cursor.Parameters.AddWithValue("$accountId", accountId);
                    cursor.Parameters.AddWithValue("$folderId", folderId);
                    promotablePriorImapFolder =
                        await cursor.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
                }
                if (!promotablePriorImapFolder)
                    throw new InvalidDataException(
                        "IMAP folder binding conflicts with an existing local or remote folder definition.");
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE folders
                    SET display_name=$displayName,
                        is_deleted_items=CASE WHEN $isDeletedItems=1 THEN 1 ELSE is_deleted_items END,
                        is_local_only=0, remote_full_name=$remoteFullName
                    WHERE folder_id=$folderId AND account_id=$accountId
                      AND is_local_only=1 AND remote_full_name IS NULL AND is_tombstoned=0;
                    """,
                    cancellationToken,
                    ("$displayName", displayName),
                    ("$isDeletedItems", designateAsDeletedItems ? 1 : 0),
                    ("$remoteFullName", remoteFullName),
                    ("$folderId", folderId),
                    ("$accountId", accountId)).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureLocalFolderAsync(
        string accountId,
        string folderId,
        string displayName,
        string? parentFolderId,
        bool isDeletedItems,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(accountId, 64, nameof(accountId));
        ValidateMailboxMutationIdentifier(folderId, 512, nameof(folderId));
        ValidateBoundedProfileText(displayName, 512, nameof(displayName));
        if (parentFolderId is not null)
            ValidateMailboxMutationIdentifier(parentFolderId, 512, nameof(parentFolderId));
        if (isDeletedItems && parentFolderId is not null)
            throw new InvalidDataException(
                "The Deleted Items folder must be created at the local account root.");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO accounts(account_id) VALUES ($accountId);",
            cancellationToken, ("$accountId", accountId)).ConfigureAwait(false);
        if (parentFolderId is not null)
        {
            await EnsureActiveManagedLocalParentAsync(
                connection, transaction, accountId, parentFolderId, cancellationToken).ConfigureAwait(false);
        }
        if (isDeletedItems)
        {
            await using var existingDeleted = connection.CreateCommand();
            existingDeleted.Transaction = transaction;
            existingDeleted.CommandText =
                "SELECT 1 FROM folders WHERE account_id=$accountId AND is_deleted_items=1 AND is_tombstoned=0;";
            existingDeleted.Parameters.AddWithValue("$accountId", accountId);
            if (await existingDeleted.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException(
                    "This account already has a designated Deleted Items folder.");
        }
        var inserted = await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO folders(
                folder_id, account_id, parent_folder_id, display_name, is_deleted_items)
            VALUES ($folderId, $accountId, $parentFolderId, $displayName, $isDeletedItems)
            ON CONFLICT(folder_id) DO NOTHING;
            """,
            cancellationToken,
            ("$folderId", folderId),
            ("$accountId", accountId),
            ("$parentFolderId", (object?)parentFolderId ?? DBNull.Value),
            ("$displayName", displayName),
            ("$isDeletedItems", isDeletedItems ? 1 : 0)).ConfigureAwait(false);
        if (inserted != 1)
            throw new InvalidOperationException(
                "Local folder identifier already exists; folder definitions are not overwritten.");
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO local_folder_revisions(
                folder_id, revision, display_name, is_tombstoned, saved_utc)
            VALUES ($folderId, 1, $displayName, 0, $savedUtc);
            """,
            cancellationToken,
            ("$folderId", folderId),
            ("$displayName", displayName),
            ("$savedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)))
            .ConfigureAwait(false);
        await InsertLocalFolderHierarchyRevisionAsync(
            connection, transaction, folderId, parentFolderId, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredLocalFolderRevision> RenameLocalFolderAsync(
        string accountId,
        string folderId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(accountId, 64, nameof(accountId));
        ValidateMailboxMutationIdentifier(folderId, 512, nameof(folderId));
        ValidateBoundedProfileText(displayName, 512, nameof(displayName));
        var normalizedName = displayName.Trim();
        if (normalizedName.Length == 0)
            throw new InvalidDataException("Local folder name is required.");

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadManagedLocalFolderAsync(
            connection, transaction, accountId, folderId, cancellationToken).ConfigureAwait(false);
        if (current.IsTombstoned)
            throw new InvalidOperationException("A retired local folder cannot be renamed or resurrected.");
        if (current.DisplayName.Equals(normalizedName, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new StoredLocalFolderRevision(
                folderId, current.Revision, current.DisplayName, false, current.SavedUtc);
        }

        var savedUtc = DateTimeOffset.UtcNow;
        var revision = checked(current.Revision + 1);
        await InsertLocalFolderRevisionAsync(
            connection, transaction, folderId, revision, normalizedName,
            isTombstoned: false, savedUtc, cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE folders SET display_name=$displayName
            WHERE folder_id=$folderId AND account_id=$accountId
              AND is_local_only=1 AND remote_full_name IS NULL AND is_tombstoned=0;
            """,
            cancellationToken,
            ("$displayName", normalizedName),
            ("$folderId", folderId),
            ("$accountId", accountId)).ConfigureAwait(false);
        if (updated != 1)
            throw new InvalidOperationException("Local folder changed while its revision was being saved.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoredLocalFolderRevision(folderId, revision, normalizedName, false, savedUtc);
    }

    public async Task<StoredLocalFolderRevision> TombstoneLocalFolderAsync(
        string accountId,
        string folderId,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(accountId, 64, nameof(accountId));
        ValidateMailboxMutationIdentifier(folderId, 512, nameof(folderId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadManagedLocalFolderAsync(
            connection, transaction, accountId, folderId, cancellationToken).ConfigureAwait(false);
        if (current.IsTombstoned)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new StoredLocalFolderRevision(
                folderId, current.Revision, current.DisplayName, true, current.SavedUtc);
        }
        if (current.IsDeletedItems)
            throw new InvalidOperationException(
                "The Deleted Items folder cannot be retired while no-purge behavior is required.");
        if (await HasActiveChildFolderAsync(
                connection, transaction, accountId, folderId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A local folder with active child folders cannot be retired.");
        if (await HasUnresolvedFolderOperationAsync(
                connection, transaction, folderId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "A local folder with unresolved mailbox operations cannot be retired.");

        var savedUtc = DateTimeOffset.UtcNow;
        var revision = checked(current.Revision + 1);
        await InsertLocalFolderRevisionAsync(
            connection, transaction, folderId, revision, current.DisplayName,
            isTombstoned: true, savedUtc, cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE folders SET is_tombstoned=1
            WHERE folder_id=$folderId AND account_id=$accountId
              AND is_local_only=1 AND remote_full_name IS NULL AND is_tombstoned=0;
            """,
            cancellationToken,
            ("$folderId", folderId),
            ("$accountId", accountId)).ConfigureAwait(false);
        if (updated != 1)
            throw new InvalidOperationException("Local folder changed while its tombstone was being saved.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoredLocalFolderRevision(folderId, revision, current.DisplayName, true, savedUtc);
    }

    public async Task<IReadOnlyList<StoredLocalFolderRevision>> ReadLocalFolderRevisionHistoryAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(folderId, 512, nameof(folderId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT revision, display_name, is_tombstoned, saved_utc
            FROM local_folder_revisions
            WHERE folder_id=$folderId
            ORDER BY revision;
            """;
        command.Parameters.AddWithValue("$folderId", folderId);
        var revisions = new List<StoredLocalFolderRevision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            revisions.Add(new StoredLocalFolderRevision(
                folderId,
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt64(2) != 0,
                DateTimeOffset.Parse(
                    reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return revisions;
    }

    public async Task<StoredLocalFolderHierarchyRevision> MoveLocalFolderAsync(
        string accountId,
        string folderId,
        string? parentFolderId,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(accountId, 64, nameof(accountId));
        ValidateMailboxMutationIdentifier(folderId, 512, nameof(folderId));
        if (parentFolderId is not null)
            ValidateMailboxMutationIdentifier(parentFolderId, 512, nameof(parentFolderId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadManagedLocalFolderAsync(
            connection, transaction, accountId, folderId, cancellationToken).ConfigureAwait(false);
        if (current.IsTombstoned)
            throw new InvalidOperationException("A retired local folder cannot be moved or resurrected.");
        if (current.IsDeletedItems)
            throw new InvalidOperationException(
                "The Deleted Items folder must remain at the local account root.");
        if (string.Equals(parentFolderId, folderId, StringComparison.Ordinal))
            throw new InvalidDataException("A local folder cannot be its own parent.");
        if (string.Equals(current.ParentFolderId, parentFolderId, StringComparison.Ordinal))
        {
            var existing = await ReadLatestLocalFolderHierarchyRevisionAsync(
                connection, transaction, folderId, cancellationToken).ConfigureAwait(false);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }
        if (parentFolderId is not null)
        {
            await EnsureActiveManagedLocalParentAsync(
                connection, transaction, accountId, parentFolderId, cancellationToken).ConfigureAwait(false);
            await EnsureNoLocalFolderHierarchyCycleAsync(
                connection, transaction, accountId, folderId, parentFolderId, cancellationToken).ConfigureAwait(false);
        }

        var savedUtc = DateTimeOffset.UtcNow;
        await InsertLocalFolderHierarchyRevisionAsync(
            connection, transaction, folderId, parentFolderId, savedUtc, cancellationToken).ConfigureAwait(false);
        var updated = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE folders SET parent_folder_id=$parentFolderId
            WHERE folder_id=$folderId AND account_id=$accountId
              AND is_local_only=1 AND remote_full_name IS NULL AND is_tombstoned=0
              AND is_deleted_items=0;
            """,
            cancellationToken,
            ("$parentFolderId", (object?)parentFolderId ?? DBNull.Value),
            ("$folderId", folderId),
            ("$accountId", accountId)).ConfigureAwait(false);
        if (updated != 1)
            throw new InvalidOperationException("Local folder changed while its hierarchy revision was being saved.");
        var revision = await ReadLatestLocalFolderHierarchyRevisionAsync(
            connection, transaction, folderId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revision;
    }

    public async Task<IReadOnlyList<StoredLocalFolderHierarchyRevision>> ReadLocalFolderHierarchyHistoryAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        ValidateMailboxMutationIdentifier(folderId, 512, nameof(folderId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sequence, parent_folder_id, saved_utc
            FROM local_folder_hierarchy_revisions
            WHERE folder_id=$folderId
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$folderId", folderId);
        var revisions = new List<StoredLocalFolderHierarchyRevision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            revisions.Add(new StoredLocalFolderHierarchyRevision(
                reader.GetInt64(0),
                folderId,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.Parse(
                    reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return revisions;
    }

    private static async Task<ManagedLocalFolderState> ReadManagedLocalFolderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        string folderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT f.display_name, f.parent_folder_id, f.is_deleted_items, f.is_tombstoned,
                   r.revision, r.saved_utc
            FROM folders f
            JOIN local_folder_revisions r ON r.folder_id=f.folder_id
            WHERE f.folder_id=$folderId AND f.account_id=$accountId
              AND f.is_local_only=1 AND f.remote_full_name IS NULL
              AND r.revision=(
                  SELECT MAX(latest.revision) FROM local_folder_revisions latest
                  WHERE latest.folder_id=f.folder_id);
            """;
        command.Parameters.AddWithValue("$folderId", folderId);
        command.Parameters.AddWithValue("$accountId", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Only protected user-created local folders can be renamed or retired; migrated and IMAP folders are read-only here.");
        return new ManagedLocalFolderState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2) != 0,
            reader.GetInt64(3) != 0,
            reader.GetInt32(4),
            DateTimeOffset.Parse(
                reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static async Task InsertLocalFolderRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string folderId,
        int revision,
        string displayName,
        bool isTombstoned,
        DateTimeOffset savedUtc,
        CancellationToken cancellationToken) =>
        _ = await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO local_folder_revisions(
                folder_id, revision, display_name, is_tombstoned, saved_utc)
            VALUES ($folderId, $revision, $displayName, $isTombstoned, $savedUtc);
            """,
            cancellationToken,
            ("$folderId", folderId),
            ("$revision", revision),
            ("$displayName", displayName),
            ("$isTombstoned", isTombstoned ? 1 : 0),
            ("$savedUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);

    private static async Task InsertLocalFolderHierarchyRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string folderId,
        string? parentFolderId,
        DateTimeOffset savedUtc,
        CancellationToken cancellationToken) =>
        _ = await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO local_folder_hierarchy_revisions(folder_id, parent_folder_id, saved_utc)
            VALUES ($folderId, $parentFolderId, $savedUtc);
            """,
            cancellationToken,
            ("$folderId", folderId),
            ("$parentFolderId", (object?)parentFolderId ?? DBNull.Value),
            ("$savedUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);

    private static async Task<StoredLocalFolderHierarchyRevision> ReadLatestLocalFolderHierarchyRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string folderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT sequence, parent_folder_id, saved_utc
            FROM local_folder_hierarchy_revisions
            WHERE folder_id=$folderId
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$folderId", folderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Local folder hierarchy history is missing.");
        return new StoredLocalFolderHierarchyRevision(
            reader.GetInt64(0),
            folderId,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            DateTimeOffset.Parse(
                reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static async Task EnsureActiveManagedLocalParentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        string parentFolderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT 1
            FROM folders f
            JOIN local_folder_revisions r ON r.folder_id=f.folder_id
            WHERE f.folder_id=$folderId AND f.account_id=$accountId
              AND f.is_local_only=1 AND f.remote_full_name IS NULL
              AND f.is_tombstoned=0 AND f.is_deleted_items=0
              AND r.revision=(
                  SELECT MAX(latest.revision) FROM local_folder_revisions latest
                  WHERE latest.folder_id=f.folder_id)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$folderId", parentFolderId);
        command.Parameters.AddWithValue("$accountId", accountId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidDataException(
                "Local folder parent must be an active non-Deleted-Items user-created folder in the same account.");
    }

    private static async Task EnsureNoLocalFolderHierarchyCycleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        string folderId,
        string candidateParentFolderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            WITH RECURSIVE ancestors(folder_id, parent_folder_id, depth) AS (
                SELECT folder_id, parent_folder_id, 0
                FROM folders
                WHERE folder_id=$candidateParentFolderId AND account_id=$accountId
                UNION ALL
                SELECT f.folder_id, f.parent_folder_id, ancestors.depth + 1
                FROM folders f
                JOIN ancestors ON f.folder_id=ancestors.parent_folder_id
                WHERE f.account_id=$accountId AND ancestors.depth < 64
            )
            SELECT
                MAX(CASE WHEN folder_id=$folderId THEN 1 ELSE 0 END),
                MAX(CASE WHEN depth=64 AND parent_folder_id IS NOT NULL THEN 1 ELSE 0 END)
            FROM ancestors;
            """;
        command.Parameters.AddWithValue("$candidateParentFolderId", candidateParentFolderId);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$folderId", folderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Local folder hierarchy validation did not return a result.");
        if (!reader.IsDBNull(0) && reader.GetInt64(0) != 0)
            throw new InvalidDataException("Moving this local folder would create a hierarchy cycle.");
        if (!reader.IsDBNull(1) && reader.GetInt64(1) != 0)
            throw new InvalidDataException("Local folder hierarchy exceeds the 64-level safety limit.");
    }

    private static async Task<bool> HasActiveChildFolderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        string folderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT 1 FROM folders
            WHERE account_id=$accountId AND parent_folder_id=$folderId AND is_tombstoned=0
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$folderId", folderId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> HasUnresolvedFolderOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string folderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT 1
            FROM mailbox_mutation_operations operation
            WHERE (operation.source_folder_id=$folderId OR operation.target_folder_id=$folderId)
              AND COALESCE((
                  SELECT journal.state FROM operation_journal journal
                  WHERE journal.operation_id=operation.operation_id
                  ORDER BY journal.sequence DESC LIMIT 1), 'Queued')
                  NOT IN ('Acknowledged','Failed')
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$folderId", folderId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private sealed record ManagedLocalFolderState(
        string DisplayName,
        string? ParentFolderId,
        bool IsDeletedItems,
        bool IsTombstoned,
        int Revision,
        DateTimeOffset SavedUtc);

    private async Task<long> GetScalarCountAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private static string BuildFtsQuery(string query)
    {
        if (query.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(query), "Search text exceeds 512 characters.");
        var tokens = Regex.Matches(query, @"[\p{L}\p{N}_@.+-]{1,64}", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Take(12)
            .ToArray();
        return string.Join(" AND ", tokens.Select(token => $"\"{token}\"*"));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<long> ReadSqliteDataVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private long GetSqliteDurableByteCount()
    {
        var database = new FileInfo(DatabasePath);
        var writeAheadLog = new FileInfo(DatabasePath + "-wal");
        return checked(database.Length + (writeAheadLog.Exists ? writeAheadLog.Length : 0));
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string alterSql,
        CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var found = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            found |= reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (found) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = alterSql;
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureMailboxMutationSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var definition = connection.CreateCommand();
        definition.CommandText =
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='mailbox_mutation_operations';";
        var sql = (string?)await definition.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("Mailbox mutation operation schema is missing.");
        if (sql.Contains("'RetainDeletedHistory'", StringComparison.Ordinal)) return;
        foreach (var requiredKind in new[]
        {
            "'MoveToFolder'", "'SetFlag'", "'ClearFlag'", "'MoveToDeletedItems'",
        })
        {
            if (!sql.Contains(requiredKind, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Existing mailbox mutation schema is not the supported pre-retention shape.");
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name='mailbox_mutation_operations_upgrade';";
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidDataException("Mailbox mutation schema upgrade staging table already exists.");
        command.CommandText =
            """
            CREATE TABLE mailbox_mutation_operations_upgrade(
                operation_id TEXT PRIMARY KEY REFERENCES pending_operations(operation_id),
                account_id TEXT NOT NULL REFERENCES accounts(account_id),
                message_id TEXT NOT NULL REFERENCES messages(message_id),
                source_folder_id TEXT NOT NULL REFERENCES folders(folder_id),
                mutation_kind TEXT NOT NULL CHECK(mutation_kind IN (
                    'MoveToFolder','SetFlag','ClearFlag','MoveToDeletedItems','RetainDeletedHistory')),
                target_folder_id TEXT NULL REFERENCES folders(folder_id),
                flag_name TEXT NULL,
                expected_uid_validity TEXT NULL,
                expected_uid TEXT NULL,
                CHECK (
                    (mutation_kind IN ('MoveToFolder','MoveToDeletedItems')
                        AND target_folder_id IS NOT NULL AND flag_name IS NULL)
                    OR
                    (mutation_kind IN ('SetFlag','ClearFlag')
                        AND target_folder_id IS NULL AND flag_name IS NOT NULL)
                    OR
                    (mutation_kind='RetainDeletedHistory'
                        AND target_folder_id IS NULL AND flag_name IS NULL)
                )
            ) STRICT;
            INSERT INTO mailbox_mutation_operations_upgrade(
                operation_id, account_id, message_id, source_folder_id, mutation_kind,
                target_folder_id, flag_name, expected_uid_validity, expected_uid)
            SELECT operation_id, account_id, message_id, source_folder_id, mutation_kind,
                target_folder_id, flag_name, expected_uid_validity, expected_uid
            FROM mailbox_mutation_operations;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM mailbox_mutation_operations) =
                    (SELECT COUNT(*) FROM mailbox_mutation_operations_upgrade)
                AND NOT EXISTS (
                    SELECT operation_id, account_id, message_id, source_folder_id, mutation_kind,
                        target_folder_id, flag_name, expected_uid_validity, expected_uid
                    FROM mailbox_mutation_operations
                    EXCEPT
                    SELECT operation_id, account_id, message_id, source_folder_id, mutation_kind,
                        target_folder_id, flag_name, expected_uid_validity, expected_uid
                    FROM mailbox_mutation_operations_upgrade)
                AND NOT EXISTS (
                    SELECT operation_id, account_id, message_id, source_folder_id, mutation_kind,
                        target_folder_id, flag_name, expected_uid_validity, expected_uid
                    FROM mailbox_mutation_operations_upgrade
                    EXCEPT
                    SELECT operation_id, account_id, message_id, source_folder_id, mutation_kind,
                        target_folder_id, flag_name, expected_uid_validity, expected_uid
                    FROM mailbox_mutation_operations);
            """;
        if (Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException("Mailbox mutation schema upgrade did not preserve every row exactly.");
        command.CommandText =
            """
            DROP TABLE mailbox_mutation_operations;
            ALTER TABLE mailbox_mutation_operations_upgrade RENAME TO mailbox_mutation_operations;
            CREATE TRIGGER immutable_mailbox_mutations_update
            BEFORE UPDATE ON mailbox_mutation_operations
            BEGIN SELECT RAISE(ABORT, 'mailbox mutation records are immutable'); END;
            CREATE TRIGGER immutable_mailbox_mutations_delete
            BEFORE DELETE ON mailbox_mutation_operations
            BEGIN SELECT RAISE(ABORT, 'mailbox mutation records cannot be deleted'); END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "PRAGMA foreign_key_check;";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Mailbox mutation schema upgrade failed foreign-key validation.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredMailboxMutationOperation> ReadMailboxMutationByIdempotencyKeyAsync(
        string idempotencyKey,
        string expectedPayloadSha256,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.operation_id, p.idempotency_key, m.account_id, m.message_id,
                   m.source_folder_id, m.mutation_kind, m.target_folder_id, m.flag_name,
                   m.expected_uid_validity, m.expected_uid, latest.state, latest.event_utc,
                   latest.server_acknowledgement, latest.error_code, p.operation_type, p.payload_sha256
            FROM pending_operations p
            JOIN mailbox_mutation_operations m ON m.operation_id=p.operation_id
            JOIN operation_journal latest ON latest.sequence=(
                SELECT MAX(j.sequence) FROM operation_journal j WHERE j.operation_id=p.operation_id)
            WHERE p.idempotency_key=$idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !reader.GetString(14).Equals("MailboxMutation", StringComparison.Ordinal) ||
            !reader.GetString(15).Equals(expectedPayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Idempotency key was reused for a different mailbox mutation payload.");
        return ReadMailboxMutation(reader, wasAlreadyPresent: true);
    }

    private static StoredMailboxMutationOperation ReadMailboxMutation(
        SqliteDataReader reader,
        bool wasAlreadyPresent)
    {
        if (!Enum.TryParse<MailboxMutationKind>(reader.GetString(5), ignoreCase: false, out var kind) ||
            !Enum.TryParse<OperationEventState>(reader.GetString(10), ignoreCase: false, out var state))
            throw new InvalidDataException("Mailbox mutation journal contains an unknown kind or state.");
        return new StoredMailboxMutationOperation(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), kind,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : ulong.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
            state,
            DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            wasAlreadyPresent);
    }

    private static void ValidateMailAccountProfile(MailAccountProfileInput profile)
    {
        if (profile.AccountId.Length is < 1 or > 64 || profile.AccountId.Any(character =>
            !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new InvalidDataException("Mail account identifier is invalid.");
        ValidateBoundedProfileText(profile.DisplayName, 256, nameof(profile.DisplayName));
        ValidateBoundedProfileText(profile.ReceiveHost, 255, nameof(profile.ReceiveHost));
        ValidateBoundedProfileText(profile.SmtpHost, 255, nameof(profile.SmtpHost));
        ValidateMailHost(profile.ReceiveHost, nameof(profile.ReceiveHost));
        ValidateMailHost(profile.SmtpHost, nameof(profile.SmtpHost));
        ValidateBoundedProfileText(profile.CredentialUserName, 512, nameof(profile.CredentialUserName));
        ValidateBoundedProfileText(profile.SenderAddress, 512, nameof(profile.SenderAddress));
        if (!System.Net.Mail.MailAddress.TryCreate(profile.SenderAddress.Trim(), out var senderAddress) ||
            !senderAddress.Address.Equals(profile.SenderAddress.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sender address must be one mailbox without a display name.");
        if (profile.ReceiveProtocol is not ("Imap" or "Pop3"))
            throw new InvalidDataException("Receive protocol must be Imap or Pop3.");
        if (profile.ReceiveTlsMode is not ("ImplicitTls" or "RequiredStartTls") ||
            profile.SmtpTlsMode is not ("ImplicitTls" or "RequiredStartTls"))
            throw new InvalidDataException("Mail endpoints require an explicit validated TLS mode.");
    }

    private static void ValidateMailHost(string host, string fieldName)
    {
        if (System.Net.IPAddress.TryParse(host, out _)) return;
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new InvalidDataException($"{fieldName} is not a valid DNS host name or IP address.");
    }

    private static bool IsLowercaseSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateMailboxMutationIntent(OfflineMailboxMutationIntent intent)
    {
        ValidateMailboxMutationIdentifier(intent.AccountId, 64, nameof(intent.AccountId));
        ValidateMailboxMutationIdentifier(intent.MessageId, 64, nameof(intent.MessageId));
        ValidateMailboxMutationIdentifier(intent.SourceFolderId, 512, nameof(intent.SourceFolderId));
        if (intent.TargetFolderId is not null)
            ValidateMailboxMutationIdentifier(intent.TargetFolderId, 512, nameof(intent.TargetFolderId));
        var isMove = intent.Kind is MailboxMutationKind.MoveToFolder or MailboxMutationKind.MoveToDeletedItems;
        var isFlag = intent.Kind is MailboxMutationKind.SetFlag or MailboxMutationKind.ClearFlag;
        var isRetainedHistory = intent.Kind == MailboxMutationKind.RetainDeletedHistory;
        var shapeIsValid =
            (isMove && intent.TargetFolderId is not null && intent.Flag is null) ||
            (isFlag && intent.TargetFolderId is null && intent.Flag is not null) ||
            (isRetainedHistory && intent.TargetFolderId is null && intent.Flag is null);
        if (!shapeIsValid)
            throw new InvalidDataException("Mailbox mutation payload does not match its operation kind.");
        if (intent.TargetFolderId is not null &&
            intent.TargetFolderId.Equals(intent.SourceFolderId, StringComparison.Ordinal))
            throw new InvalidDataException("Mailbox move source and target folders must differ.");
        if (intent.Flag is not null)
        {
            var normalized = NormalizeRemoteFlags([intent.Flag]);
            if (normalized.Length != 1 || normalized[0] is "\\Deleted" or "\\Recent")
                throw new InvalidDataException("Mailbox flag mutation is invalid or reserved.");
        }
    }

    private static void ValidateMailboxMutationIdentifier(string value, int maximumLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => char.IsControl(character)))
            throw new InvalidDataException($"Mailbox mutation field {fieldName} is invalid.");
    }

    private static void ValidateStoredMessagePage(int pageSize, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 250);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, 10_000_000);
    }

    private static void ValidateStoredFolderId(string folderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        if (folderId.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(folderId), "Folder identifiers are limited to 512 characters.");
    }

    private static string BuildSentItemsFolderId(string accountId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(accountId)));
        return $"sent-{hash[..32]}";
    }

    private static string NormalizeRfcMessageId(string value) => value.Trim().Trim('<', '>');

    private static void ValidateBoundedProfileText(string value, int maximumLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => char.IsControl(character)))
            throw new InvalidDataException($"Development account field {fieldName} is invalid.");
    }

    private static async Task ReplaceFolderMessageFlagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string folderId,
        string messageId,
        IReadOnlyCollection<string> flags,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeRemoteFlags(flags);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM folder_message_flags WHERE folder_id = $folderId AND message_id = $messageId;",
            cancellationToken, ("$folderId", folderId), ("$messageId", messageId)).ConfigureAwait(false);
        foreach (var flag in normalized)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO folder_message_flags(folder_id, message_id, flag) VALUES ($folderId, $messageId, $flag);",
                cancellationToken,
                ("$folderId", folderId), ("$messageId", messageId), ("$flag", flag)).ConfigureAwait(false);
        }
    }

    private static string[] NormalizeRemoteFlags(IReadOnlyCollection<string> flags)
    {
        ArgumentNullException.ThrowIfNull(flags);
        var normalized = flags
            .Where(flag => !string.IsNullOrWhiteSpace(flag))
            .Select(flag => flag.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length > MaximumRemoteFlags)
            throw new InvalidDataException("Remote message exceeds the configured flag-count limit.");
        if (normalized.Any(flag => flag.Length > MaximumRemoteFlagCharacters ||
            flag.Any(character => char.IsControl(character))))
            throw new InvalidDataException("Remote message contains an invalid or oversized flag.");
        return normalized;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureContentBlobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredContent stored,
        string contentKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKind);
        if (contentKind.Length > 64 || contentKind.Any(char.IsControl))
            throw new InvalidDataException("Content kind is invalid or exceeds its limit.");
        var inserted = await ExecuteAsync(connection, transaction,
            """
            INSERT INTO content_blobs(sha256, byte_count, relative_path, content_kind)
            VALUES ($sha256, $byteCount, $relativePath, $contentKind)
            ON CONFLICT(sha256) DO NOTHING;
            """,
            cancellationToken,
            ("$sha256", stored.Sha256), ("$byteCount", stored.Bytes),
            ("$relativePath", stored.RelativePath), ("$contentKind", contentKind))
            .ConfigureAwait(false);
        if (inserted == 1) return;
        if (inserted != 0)
            throw new InvalidDataException("Content catalog insertion returned an invalid row count.");

        await using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText =
            "SELECT byte_count, relative_path FROM content_blobs WHERE sha256=$sha256;";
        verify.Parameters.AddWithValue("$sha256", stored.Sha256);
        await using var reader = await verify.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetInt64(0) != stored.Bytes)
            throw new InvalidDataException(
                "Content catalog conflicts with the retained SHA-256 identity.");
        var catalogRelativePath = reader.GetString(1);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Content catalog contains a duplicate SHA-256 identity.");
        await _content.VerifyCatalogEntryAsync(
            catalogRelativePath, stored.Sha256, stored.Bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureAccountAndFolderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        string folderId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO accounts(account_id) VALUES ($accountId);",
            cancellationToken, ("$accountId", accountId)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO folders(folder_id, account_id, display_name) VALUES ($folderId, $accountId, $folderId);",
            cancellationToken, ("$folderId", folderId), ("$accountId", accountId)).ConfigureAwait(false);
    }

    private static async Task StoreNormalizedBodyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        string sender,
        string recipients,
        DateTimeOffset? sentUtc,
        string? textBody,
        string? sanitizableHtmlBody,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO normalized_message_bodies(
                message_id, sender, recipients, sent_utc, text_body, sanitizable_html_body)
            VALUES ($messageId, $sender, $recipients, $sentUtc, $textBody, $htmlBody)
            ON CONFLICT(message_id) DO UPDATE SET
                sender=excluded.sender,
                recipients=excluded.recipients,
                sent_utc=excluded.sent_utc,
                text_body=excluded.text_body,
                sanitizable_html_body=excluded.sanitizable_html_body;
            """,
            cancellationToken,
            ("$messageId", messageId), ("$sender", sender), ("$recipients", recipients),
            ("$sentUtc", sentUtc?.ToString("O", CultureInfo.InvariantCulture)),
            ("$textBody", textBody), ("$htmlBody", sanitizableHtmlBody)).ConfigureAwait(false);
    }

    private static async Task IndexSearchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        string subject,
        string sender,
        string recipients,
        string? textBody,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "DELETE FROM message_search WHERE message_id = $messageId;",
            cancellationToken, ("$messageId", messageId)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO message_search(message_id, subject, sender, recipients, text_body)
            VALUES ($messageId, $subject, $sender, $recipients, $textBody);
            """,
            cancellationToken,
            ("$messageId", messageId), ("$subject", subject), ("$sender", sender),
            ("$recipients", recipients), ("$textBody", textBody)).ConfigureAwait(false);
    }

    private async Task StoreMessageAttachmentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        IReadOnlyList<MessageAttachmentImport>? attachments,
        string contentKind,
        CancellationToken cancellationToken)
    {
        if (attachments is null) return;
        if (contentKind is not ("received-attachment" or "sent-attachment"))
            throw new ArgumentOutOfRangeException(nameof(contentKind));
        if (attachments.Count > MaximumAttachmentsPerMessage)
            throw new InvalidDataException("Message exceeds the attachment-count safety limit.");
        var orderedAttachments = attachments.OrderBy(item => item.Ordinal).ToArray();
        if (orderedAttachments.Select(item => item.Ordinal).Distinct().Count() != orderedAttachments.Length)
            throw new InvalidDataException("Message contains duplicate attachment ordinals.");
        long aggregateBytes = 0;
        foreach (var attachment in orderedAttachments)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(attachment.Ordinal);
            ArgumentException.ThrowIfNullOrWhiteSpace(attachment.FileName);
            ArgumentException.ThrowIfNullOrWhiteSpace(attachment.MediaType);
            ArgumentNullException.ThrowIfNull(attachment.Content);
            if (attachment.Content.LongLength > MaximumDecodedAttachmentBytes)
                throw new InvalidDataException("Message contains an oversized decoded attachment.");
            aggregateBytes = checked(aggregateBytes + attachment.Content.LongLength);
            if (aggregateBytes > MaximumAggregateAttachmentBytes)
                throw new InvalidDataException("Message exceeds the aggregate decoded-attachment safety limit.");
            if (attachment.FileName.Length > 512 ||
                attachment.FileName.Any(character => character is '\r' or '\n' or '\0'))
                throw new InvalidDataException("Message contains an invalid attachment filename.");
            if (!string.Equals(Path.GetFileName(attachment.FileName), attachment.FileName, StringComparison.Ordinal))
                throw new InvalidDataException("Attachment filename contains a path.");
            if (attachment.MediaType.Length > 255 || attachment.MediaType.Any(char.IsControl))
                throw new InvalidDataException("Message contains an invalid attachment media type.");
            var contentId = NormalizeAttachmentContentId(attachment.ContentId);
            var expectedHash = Convert.ToHexStringLower(SHA256.HashData(attachment.Content));
            var existingAttachment = await FindAttachmentAsync(
                connection, transaction, messageId, attachment.Ordinal, cancellationToken).ConfigureAwait(false);
            if (existingAttachment is not null &&
                (!string.Equals(existingAttachment.Value.Sha256, expectedHash, StringComparison.Ordinal) ||
                 existingAttachment.Value.ByteCount != attachment.Content.LongLength))
                throw new InvalidDataException(
                    $"Attachment ordinal {attachment.Ordinal} conflicts with previously retained bytes.");
            var stored = await _content.ImportBytesAsync(
                attachment.Content, "attachment", cancellationToken).ConfigureAwait(false);
            await EnsureContentBlobAsync(
                connection, transaction, stored, contentKind, cancellationToken)
                .ConfigureAwait(false);
            var attachmentRows = await ExecuteAsync(connection, transaction,
                """
                INSERT INTO attachments(
                    attachment_id, message_id, ordinal, file_name, media_type, byte_count, content_sha256, content_id)
                VALUES ($attachmentId, $messageId, $ordinal, $fileName, $mediaType, $byteCount, $sha256, $contentId)
                ON CONFLICT(message_id, ordinal) DO UPDATE SET
                    content_id=excluded.content_id
                WHERE attachments.content_sha256=excluded.content_sha256
                  AND attachments.byte_count=excluded.byte_count
                  AND attachments.file_name=excluded.file_name
                  AND attachments.media_type=excluded.media_type
                  AND (attachments.content_id=excluded.content_id OR attachments.content_id='');
                """,
                cancellationToken,
                ("$attachmentId", Guid.NewGuid().ToString("N")), ("$messageId", messageId),
                ("$ordinal", attachment.Ordinal), ("$fileName", attachment.FileName),
                ("$mediaType", attachment.MediaType), ("$byteCount", stored.Bytes), ("$sha256", stored.Sha256),
                ("$contentId", contentId))
                .ConfigureAwait(false);
            if (attachmentRows != 1)
                throw new InvalidDataException(
                    $"Attachment ordinal {attachment.Ordinal} conflicts with previously retained bytes.");
        }
    }

    private async Task<byte[]> ReadVerifiedContentAsync(
        string relativePath,
        string expectedSha256,
        long expectedBytes,
        string contentLabel,
        CancellationToken cancellationToken)
    {
        if (expectedBytes is < 0 or > 512L * 1024L * 1024L)
            throw new InvalidDataException($"{contentLabel} has an invalid recorded byte count.");
        var path = _content.GetAbsolutePath(relativePath);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{contentLabel} content is missing or redirected.");
        if (info.Length != expectedBytes || info.Length > int.MaxValue)
            throw new InvalidDataException($"{contentLabel} length differs from its immutable record.");
        var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"{contentLabel} SHA-256 differs from its immutable record.");
        return content;
    }

    private static string NormalizeAttachmentContentId(string? contentId)
    {
        var normalized = (contentId ?? string.Empty).Trim();
        if (normalized.Length >= 2 && normalized[0] == '<' && normalized[^1] == '>')
            normalized = normalized[1..^1].Trim();
        if (normalized.Length > 512 || normalized.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character) || character is '<' or '>'))
            throw new InvalidDataException("Attachment Content-ID is invalid or exceeds its safety limit.");
        return normalized;
    }

    private static async Task<(string Sha256, long ByteCount)?> FindAttachmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        int ordinal,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT content_sha256, byte_count FROM attachments WHERE message_id=$messageId AND ordinal=$ordinal;";
        command.Parameters.AddWithValue("$messageId", messageId);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    private static async Task RecordRemoteIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        string folderId,
        string? uidValidity,
        string? uid,
        string messageId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uidValidity) || string.IsNullOrWhiteSpace(uid)) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO message_remote_identities(account_id, folder_id, uid_validity, uid, message_id)
            VALUES ($accountId, $folderId, $uidValidity, $uid, $messageId)
            ON CONFLICT(account_id, folder_id, uid_validity, uid) DO UPDATE SET
                message_id=excluded.message_id
            WHERE message_remote_identities.message_id = excluded.message_id;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$folderId", folderId);
        command.Parameters.AddWithValue("$uidValidity", uidValidity);
        command.Parameters.AddWithValue("$uid", uid);
        command.Parameters.AddWithValue("$messageId", messageId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException("A remote IMAP identity maps to different MIME content.");
    }

    private static async Task<(string MessageId, string RawMimeSha256)?> FindByIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        string identityKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT message_id, raw_mime_sha256 FROM messages WHERE account_id = $accountId AND identity_key = $identityKey;";
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$identityKey", identityKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task<(string MessageId, string RawMimeSha256)?> FindByPopIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        string? popUidl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(popUidl)) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT message_id, raw_mime_sha256 FROM messages " +
            "WHERE account_id = $accountId AND pop_uidl = $popUidl LIMIT 1;";
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$popUidl", popUidl);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task<(string MessageId, string RawMimeSha256)?> FindByContentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        string folderId,
        string? rfcMessageId,
        string rawMimeSha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT m.message_id, m.raw_mime_sha256
            FROM messages m
            JOIN folder_messages fm ON fm.message_id = m.message_id
            WHERE m.account_id = $accountId AND fm.folder_id = $folderId
              AND m.raw_mime_sha256 = $rawMimeSha256
              AND (($rfcMessageId IS NULL AND m.rfc_message_id IS NULL) OR m.rfc_message_id = $rfcMessageId)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$folderId", folderId);
        command.Parameters.AddWithValue("$rawMimeSha256", rawMimeSha256);
        command.Parameters.AddWithValue("$rfcMessageId", (object?)rfcMessageId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static ulong ParseRetainedUInt64(string value, string label)
    {
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            !string.Equals(parsed.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Retained {label} is not canonical unsigned metadata.");
        }
        return parsed;
    }

    private static string BuildIdentityKey(
        string accountId,
        string folderId,
        string? imapUidValidity,
        string? imapUid,
        string? popUidl,
        string? rfcMessageId,
        string contentHash)
    {
        var source = string.Join('\u001f',
            accountId, folderId, imapUidValidity ?? string.Empty,
            imapUid ?? string.Empty, popUidl ?? string.Empty,
            rfcMessageId?.Trim().Trim('<', '>') ?? string.Empty, contentHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static StoredDraftRevision ReadDraftRevision(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetInt64(9) != 0);

    private static (
        string Sender,
        string SerializedRecipients,
        IReadOnlyList<string> Recipients) NormalizeEnvelope(
        string? envelopeSender,
        IReadOnlyList<string>? envelopeRecipients)
    {
        var hasSender = !string.IsNullOrWhiteSpace(envelopeSender);
        var hasRecipients = envelopeRecipients is { Count: > 0 };
        if (!hasSender && !hasRecipients)
            return (string.Empty, string.Empty, Array.Empty<string>());
        if (!hasSender || !hasRecipients)
            throw new InvalidDataException(
                "Explicit SMTP envelope routing requires both one sender and at least one recipient.");
        if (envelopeRecipients!.Count > 100)
            throw new InvalidDataException("Explicit SMTP envelope routing exceeds 100 recipients.");

        var sender = NormalizeEnvelopeMailbox(envelopeSender!, "sender");
        var recipients = envelopeRecipients
            .Select(value => NormalizeEnvelopeMailbox(value, "recipient"))
            .ToArray();
        return (sender, string.Join(EnvelopeRecipientSeparator, recipients), recipients);
    }

    private static string NormalizeEnvelopeMailbox(string value, string role)
    {
        var normalized = value.Trim();
        if (normalized.Length is 0 or > 2_048 ||
            normalized.Contains('\r') || normalized.Contains('\n') ||
            normalized.Contains(EnvelopeRecipientSeparator))
            throw new InvalidDataException($"Explicit SMTP envelope {role} is invalid.");
        return normalized;
    }

    private static string[] DeserializeEnvelopeRecipients(string serialized) =>
        string.IsNullOrEmpty(serialized)
            ? Array.Empty<string>()
            : serialized.Split(EnvelopeRecipientSeparator, StringSplitOptions.None);

    private static void ValidateDraftField(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length > maximumLength)
            throw new InvalidDataException($"Draft field {parameterName} exceeds its storage limit.");
    }

    internal static void ValidateRawMimeLength(long byteCount, string contentLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentLabel);
        if (byteCount <= 0)
            throw new InvalidDataException($"{contentLabel} cannot be empty.");
        if (byteCount > MaximumRawMimeBytes)
            throw new InvalidDataException($"{contentLabel} exceeds the 100 MiB message limit.");
    }

    private static async Task InsertCalendarEventRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        int revision,
        string title,
        DateTime startLocal,
        DateTime endLocal,
        bool allDay,
        string location,
        string notes,
        bool isTombstoned,
        DateTimeOffset savedUtc,
        CancellationToken cancellationToken) =>
        _ = await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO calendar_event_revisions(
                event_id, revision, title, start_local, end_local, all_day,
                location, notes, is_tombstoned, saved_utc)
            VALUES ($eventId, $revision, $title, $startLocal, $endLocal, $allDay,
                $location, $notes, $isTombstoned, $savedUtc);
            """,
            cancellationToken,
            ("$eventId", eventId),
            ("$revision", revision),
            ("$title", title),
            ("$startLocal", FormatCalendarLocalTimestamp(startLocal)),
            ("$endLocal", FormatCalendarLocalTimestamp(endLocal)),
            ("$allDay", allDay ? 1 : 0),
            ("$location", location),
            ("$notes", notes),
            ("$isTombstoned", isTombstoned ? 1 : 0),
            ("$savedUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);

    private async Task<StoredLocalCalendarEventRevision> ReadCalendarEventRevisionAsync(
        string eventId,
        int revision,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCalendarEventRevisionAsync(
            connection, null, eventId, revision, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoredLocalCalendarEventRevision> ReadCalendarEventRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string eventId,
        int? revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT r.event_id, r.revision, r.title, r.start_local, r.end_local,
                r.all_day, r.location, r.notes, r.is_tombstoned, r.saved_utc,
                s.source_kind, s.evidence_id, s.source_message_identity_sha256,
                s.provider_account_id, s.provider_event_id, s.provider_series_id
            FROM calendar_event_revisions r
            JOIN calendar_event_sources s ON s.event_id=r.event_id
            WHERE r.event_id=$eventId AND ($revision IS NULL OR r.revision=$revision)
            ORDER BY r.revision DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$revision", (object?)revision ?? DBNull.Value);
        var rows = await ReadCalendarEventRowsAsync(command, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1) throw new KeyNotFoundException("Calendar event revision was not found.");
        return rows[0];
    }

    private static async Task<IReadOnlyList<StoredLocalCalendarEventRevision>>
        ReadCalendarEventRowsAsync(
            SqliteCommand command,
            CancellationToken cancellationToken)
    {
        var events = new List<StoredLocalCalendarEventRevision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Enum.TryParse<CalendarEventSourceKind>(
                    reader.GetString(10), ignoreCase: false, out var sourceKind) ||
                !Enum.IsDefined(sourceKind))
            {
                throw new InvalidDataException("Stored calendar event source kind is invalid.");
            }
            events.Add(new StoredLocalCalendarEventRevision(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                ParseCalendarLocalTimestamp(reader.GetString(3)),
                ParseCalendarLocalTimestamp(reader.GetString(4)),
                reader.GetInt64(5) != 0,
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt64(8) != 0,
                sourceKind,
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                DateTimeOffset.Parse(
                    reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(15) ? null : reader.GetString(15)));
            if (events.Count > 500)
                throw new InvalidDataException("Calendar event result exceeds its bounded row limit.");
        }
        return events;
    }

    private static void ValidateLocalCalendarEventInput(
        LocalCalendarEventRevisionInput input,
        bool allowProviderSnapshot = false)
    {
        if (!Enum.IsDefined(input.SourceKind))
            throw new InvalidDataException("Calendar event source kind is invalid.");
        ValidateCalendarText(input.Title, 160, nameof(input.Title), requireValue: true);
        ValidateCalendarText(input.Location, 512, nameof(input.Location), requireValue: false);
        ValidateCalendarText(
            input.Notes, 16_384, nameof(input.Notes), requireValue: false, allowNewlines: true);
        ValidateLocalCalendarRange(input.StartLocal, input.EndLocal);
        if (input.EndLocal - input.StartLocal > TimeSpan.FromDays(366))
            throw new InvalidDataException("Calendar event duration exceeds one year.");
        if (input.AllDay &&
            (input.StartLocal.TimeOfDay != TimeSpan.Zero ||
             input.EndLocal.TimeOfDay != TimeSpan.Zero ||
             (input.EndLocal - input.StartLocal).TotalDays % 1 != 0))
        {
            throw new InvalidDataException("All-day calendar events must use whole floating-local days.");
        }

        switch (input.SourceKind)
        {
            case CalendarEventSourceKind.EmailSuggestion:
                if (input.EvidenceId is null ||
                    input.EvidenceId.Any(character => character is >= 'A' and <= 'F'))
                    throw new InvalidDataException(
                        "Email calendar suggestions require a lowercase evidence SHA-256.");
                ValidateSha256(input.EvidenceId, nameof(input.EvidenceId));
                ValidateCalendarText(
                    input.SourceMessageIdentity ?? string.Empty,
                    512,
                    nameof(input.SourceMessageIdentity),
                    requireValue: true);
                if (input.ProviderAccountId is not null || input.ProviderEventId is not null ||
                    input.ProviderSeriesId is not null)
                    throw new InvalidDataException(
                        "Email calendar suggestions cannot claim an external provider identity.");
                break;
            case CalendarEventSourceKind.Google:
            case CalendarEventSourceKind.Apple:
                ValidateLowercaseSha256(
                    input.ProviderAccountId ?? string.Empty,
                    nameof(input.ProviderAccountId));
                ValidateLowercaseSha256(
                    input.ProviderEventId ?? string.Empty,
                    nameof(input.ProviderEventId));
                if (input.ProviderSeriesId is not null)
                    ValidateLowercaseSha256(
                        input.ProviderSeriesId,
                        nameof(input.ProviderSeriesId));
                if (input.EvidenceId is not null || input.SourceMessageIdentity is not null)
                    throw new InvalidDataException(
                        "External provider calendar events cannot claim email evidence provenance.");
                if (!allowProviderSnapshot)
                    throw new InvalidOperationException(
                        "Provider calendar events must pass through the append-only provider observation boundary.");
                break;
            case CalendarEventSourceKind.LocalManual:
            case CalendarEventSourceKind.ImportedIcs:
                if (input.EvidenceId is not null || input.SourceMessageIdentity is not null ||
                    input.ProviderAccountId is not null || input.ProviderEventId is not null ||
                    input.ProviderSeriesId is not null)
                    throw new InvalidDataException(
                        "Local and imported calendar events cannot claim unrelated source provenance.");
                break;
            default:
                throw new InvalidDataException("Calendar event source kind is unsupported.");
        }
    }

    public static void ValidateCalendarProviderEventSnapshotImport(
        CalendarProviderEventSnapshotImport input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateCalendarProviderIdentity(
            input.SourceKind,
            input.ProviderAccountIdentitySha256,
            input.ProviderEventIdentitySha256);
        ValidateLowercaseSha256(
            input.ProviderRevisionIdentitySha256,
            nameof(input.ProviderRevisionIdentitySha256));
        if (input.ProviderSeriesIdentitySha256 is not null)
            ValidateLowercaseSha256(
                input.ProviderSeriesIdentitySha256,
                nameof(input.ProviderSeriesIdentitySha256));
        if (input.ObservedUtc.Offset != TimeSpan.Zero ||
            input.ObservedUtc < DateTimeOffset.UnixEpoch ||
            input.ObservedUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException(
                "Calendar provider observation time must be a bounded UTC value.");
        if (input.IsDeleted) return;
        ValidateLocalCalendarEventInput(new LocalCalendarEventRevisionInput(
            EventId: null,
            input.Title,
            input.StartLocal,
            input.EndLocal,
            input.AllDay,
            input.Location,
            input.Notes,
            input.SourceKind,
            ProviderAccountId: input.ProviderAccountIdentitySha256,
            ProviderEventId: input.ProviderEventIdentitySha256,
            ProviderSeriesId: input.ProviderSeriesIdentitySha256),
            allowProviderSnapshot: true);
    }

    public static IReadOnlyList<string> ValidateAndNormalizeCalendarProviderSeriesSnapshot(
        CalendarProviderSeriesSnapshotImport input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateCalendarProviderIdentity(
            input.SourceKind,
            input.ProviderAccountIdentitySha256,
            input.ProviderSeriesIdentitySha256);
        ValidateLowercaseSha256(
            input.ProviderSeriesRevisionIdentitySha256,
            nameof(input.ProviderSeriesRevisionIdentitySha256));
        ArgumentNullException.ThrowIfNull(input.MemberEventIdentitySha256s);
        if (input.MemberEventIdentitySha256s.Count > 500)
            throw new InvalidDataException(
                "Calendar provider series snapshot exceeds its 500-occurrence limit.");
        if (input.ObservedUtc.Offset != TimeSpan.Zero ||
            input.ObservedUtc < DateTimeOffset.UnixEpoch ||
            input.ObservedUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new InvalidDataException(
                "Calendar provider series observation time must be a bounded UTC value.");
        }
        var members = input.MemberEventIdentitySha256s.ToArray();
        foreach (var member in members)
            ValidateLowercaseSha256(member, "Provider series member event identity");
        Array.Sort(members, StringComparer.Ordinal);
        if (members.Distinct(StringComparer.Ordinal).Count() != members.Length)
            throw new InvalidDataException(
                "Calendar provider series snapshot contains a duplicate occurrence identity.");
        return members;
    }

    private static void ValidateCalendarProviderIdentity(
        CalendarEventSourceKind sourceKind,
        string providerAccountIdentitySha256,
        string providerEventIdentitySha256)
    {
        if (sourceKind is not CalendarEventSourceKind.Google and
            not CalendarEventSourceKind.Apple)
            throw new InvalidDataException(
                "Calendar provider observations support only Google or Apple provenance.");
        ValidateLowercaseSha256(
            providerAccountIdentitySha256, nameof(providerAccountIdentitySha256));
        ValidateLowercaseSha256(
            providerEventIdentitySha256, nameof(providerEventIdentitySha256));
    }

    private static void ValidateLowercaseSha256(string value, string fieldName)
    {
        ValidateSha256(value, fieldName);
        if (value.Any(character => character is >= 'A' and <= 'F'))
            throw new InvalidDataException($"{fieldName} must be a lowercase SHA-256 value.");
    }

    private static string HashCalendarProviderEventSnapshot(
        CalendarProviderEventSnapshotImport input)
    {
        var canonical = input.ProviderSeriesIdentitySha256 is null
            ? JsonSerializer.Serialize(new
            {
                SourceKind = input.SourceKind.ToString(),
                input.ProviderAccountIdentitySha256,
                input.ProviderEventIdentitySha256,
                input.ProviderRevisionIdentitySha256,
                Title = input.IsDeleted ? null : input.Title.Trim(),
                StartLocal = input.IsDeleted ? null : FormatCalendarLocalTimestamp(input.StartLocal),
                EndLocal = input.IsDeleted ? null : FormatCalendarLocalTimestamp(input.EndLocal),
                AllDay = input.IsDeleted ? (bool?)null : input.AllDay,
                Location = input.IsDeleted ? null : input.Location.Trim(),
                Notes = input.IsDeleted ? null : input.Notes,
                input.IsDeleted,
            })
            : JsonSerializer.Serialize(new
        {
            SourceKind = input.SourceKind.ToString(),
            input.ProviderAccountIdentitySha256,
            input.ProviderEventIdentitySha256,
            input.ProviderRevisionIdentitySha256,
            input.ProviderSeriesIdentitySha256,
            Title = input.IsDeleted ? null : input.Title.Trim(),
            StartLocal = input.IsDeleted ? null : FormatCalendarLocalTimestamp(input.StartLocal),
            EndLocal = input.IsDeleted ? null : FormatCalendarLocalTimestamp(input.EndLocal),
            AllDay = input.IsDeleted ? (bool?)null : input.AllDay,
            Location = input.IsDeleted ? null : input.Location.Trim(),
            Notes = input.IsDeleted ? null : input.Notes,
            input.IsDeleted,
        });
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string HashCalendarProviderSeriesSnapshot(
        CalendarProviderSeriesSnapshotImport input,
        IReadOnlyList<string> normalizedMembers)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            SourceKind = input.SourceKind.ToString(),
            input.ProviderAccountIdentitySha256,
            input.ProviderSeriesIdentitySha256,
            input.ProviderSeriesRevisionIdentitySha256,
            MemberEventIdentitySha256s = normalizedMembers,
        });
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool CalendarEventContentMatches(
        StoredLocalCalendarEventRevision current,
        CalendarProviderEventSnapshotImport input) =>
        string.Equals(current.Title, input.Title.Trim(), StringComparison.Ordinal) &&
        current.StartLocal == input.StartLocal &&
        current.EndLocal == input.EndLocal &&
        current.AllDay == input.AllDay &&
        string.Equals(current.Location, input.Location.Trim(), StringComparison.Ordinal) &&
        string.Equals(current.Notes, input.Notes, StringComparison.Ordinal);

    private static void ValidateLocalCalendarRange(DateTime startLocal, DateTime endLocal)
    {
        if (startLocal.Kind != DateTimeKind.Unspecified || endLocal.Kind != DateTimeKind.Unspecified ||
            startLocal < new DateTime(1970, 1, 1) ||
            endLocal > new DateTime(2101, 1, 1) ||
            endLocal <= startLocal)
        {
            throw new InvalidDataException(
                "Calendar ranges must be increasing bounded floating-local date/time values.");
        }
    }

    private static void ValidateCalendarText(
        string value,
        int maximumLength,
        string fieldName,
        bool requireValue,
        bool allowNewlines = false)
    {
        ArgumentNullException.ThrowIfNull(value, fieldName);
        var hasProhibitedControl = value.Any(character =>
            char.IsControl(character) &&
            !(allowNewlines && character is '\r' or '\n' or '\t'));
        if ((requireValue && string.IsNullOrWhiteSpace(value)) || value.Length > maximumLength ||
            hasProhibitedControl)
            throw new InvalidDataException($"Calendar field {fieldName} is invalid or exceeds its limit.");
    }

    private static string NormalizeCalendarEventId(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        var normalized = eventId.Trim().ToLowerInvariant();
        if (!Guid.TryParseExact(normalized, "N", out _))
            throw new InvalidDataException("Calendar event identifier is invalid.");
        return normalized;
    }

    private static string FormatCalendarLocalTimestamp(DateTime value) =>
        value.ToString(CalendarLocalTimestampFormat, CultureInfo.InvariantCulture);

    private static DateTime ParseCalendarLocalTimestamp(string value) =>
        DateTime.ParseExact(
            value,
            CalendarLocalTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);

    private sealed record ContactBaseRow(
        string ContactId,
        int Revision,
        string DisplayName,
        string EmailAddress,
        string Notes,
        bool IsGroup,
        bool IsTombstoned,
        DateTimeOffset SavedUtc);

    private static async Task InsertContactRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contactId,
        int revision,
        string displayName,
        string emailAddress,
        string notes,
        bool isGroup,
        bool isTombstoned,
        IReadOnlyList<ContactGroupMemberInput> groupMembers,
        DateTimeOffset savedUtc,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO contact_revisions(
                contact_id, revision, display_name, email_address, notes,
                is_group, is_tombstoned, saved_utc)
            VALUES ($contactId, $revision, $displayName, $emailAddress, $notes,
                $isGroup, $isTombstoned, $savedUtc);
            """,
            cancellationToken,
            ("$contactId", contactId), ("$revision", revision), ("$displayName", displayName),
            ("$emailAddress", emailAddress), ("$notes", notes), ("$isGroup", isGroup ? 1 : 0),
            ("$isTombstoned", isTombstoned ? 1 : 0),
            ("$savedUtc", savedUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        for (var ordinal = 0; ordinal < groupMembers.Count; ordinal++)
        {
            var member = groupMembers[ordinal];
            await ExecuteAsync(
                connection, transaction,
                """
                INSERT INTO contact_revision_group_members(
                    contact_id, revision, ordinal, member_kind, member_value)
                VALUES ($contactId, $revision, $ordinal, $memberKind, $memberValue);
                """,
                cancellationToken,
                ("$contactId", contactId), ("$revision", revision), ("$ordinal", ordinal),
                ("$memberKind", member.Kind.ToString()), ("$memberValue", member.Value))
                .ConfigureAwait(false);
        }
    }

    private async Task<StoredContactRevision> ReadContactRevisionAsync(
        string contactId,
        int revision,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.contact_id, r.revision, r.display_name, r.email_address, r.notes,
                r.is_group, r.is_tombstoned, r.saved_utc
            FROM contact_revisions r
            WHERE r.contact_id=$contactId AND r.revision=$revision;
            """;
        command.Parameters.AddWithValue("$contactId", contactId);
        command.Parameters.AddWithValue("$revision", revision);
        var rows = await ReadContactBaseRowsAsync(command, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1) throw new KeyNotFoundException("Contact revision was not found.");
        return (await MaterializeContactRowsAsync(connection, rows, cancellationToken).ConfigureAwait(false))[0];
    }

    private static async Task<List<ContactBaseRow>> ReadContactBaseRowsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<ContactBaseRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ContactBaseRow(
                reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5) != 0, reader.GetInt64(6) != 0,
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<StoredContactRevision>> MaterializeContactRowsAsync(
        SqliteConnection connection,
        IReadOnlyList<ContactBaseRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        var contactIds = rows.Select(row => row.ContactId).Distinct(StringComparer.Ordinal).ToArray();
        var placeholders = string.Join(",", contactIds.Select((_, index) => $"$contact{index}"));
        var membersByRevision = new Dictionary<(string ContactId, int Revision), List<StoredContactGroupMember>>();
        await using (var group = connection.CreateCommand())
        {
            group.CommandText =
                $"""
                SELECT contact_id, revision, ordinal, member_kind, member_value
                FROM contact_revision_group_members
                WHERE contact_id IN ({placeholders})
                ORDER BY contact_id, revision, ordinal;
                """;
            for (var index = 0; index < contactIds.Length; index++)
                group.Parameters.AddWithValue($"$contact{index}", contactIds[index]);
            await using var reader = await group.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = (reader.GetString(0), reader.GetInt32(1));
                if (!membersByRevision.TryGetValue(key, out var members))
                {
                    members = [];
                    membersByRevision.Add(key, members);
                }
                if (!Enum.TryParse<ContactGroupMemberKind>(reader.GetString(3), out var kind))
                    throw new InvalidDataException("Stored contact group member kind is invalid.");
                members.Add(new StoredContactGroupMember(
                    reader.GetInt32(2), kind, reader.GetString(4)));
                if (members.Count > 10_000)
                    throw new InvalidDataException("Stored contact group member count exceeds its limit.");
            }
        }
        var results = new List<StoredContactRevision>(rows.Count);
        foreach (var row in rows)
        {
            var members = membersByRevision.GetValueOrDefault((row.ContactId, row.Revision)) ?? [];
            results.Add(new StoredContactRevision(
                row.ContactId, row.Revision, row.DisplayName, row.EmailAddress, row.Notes,
                row.IsGroup, row.IsTombstoned, row.SavedUtc, members));
        }
        return results;
    }

    private static void ValidateContactRevisionInput(ContactRevisionInput input)
    {
        ValidateContactText(input.DisplayName, 512, nameof(input.DisplayName), requireValue: true);
        ValidateContactText(input.EmailAddress, 2_048, nameof(input.EmailAddress), requireValue: false);
        ValidateContactText(input.Notes, 16_384, nameof(input.Notes), requireValue: false, allowNewlines: true);
        var members = input.GroupMembers ?? [];
        if (!input.IsGroup && members.Count != 0)
            throw new InvalidDataException("An individual contact cannot contain group members.");
        ValidateContactGroupMembers(members);
    }

    private static void ValidateContactGroupMembers(IReadOnlyList<ContactGroupMemberInput> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count > 10_000)
            throw new InvalidDataException("Contact group member count exceeds its limit.");
        foreach (var member in members)
        {
            if (!Enum.IsDefined(member.Kind))
                throw new InvalidDataException("Contact group member kind is invalid.");
            ValidateContactText(member.Value, 4_096, "group member", requireValue: true);
        }
    }

    private static void ValidateContactText(
        string value,
        int maximumLength,
        string fieldName,
        bool requireValue,
        bool allowNewlines = false)
    {
        ArgumentNullException.ThrowIfNull(value, fieldName);
        if ((requireValue && string.IsNullOrWhiteSpace(value)) || value.Length > maximumLength ||
            value.Contains('\0') || (!allowNewlines && value.Any(character => character is '\r' or '\n')))
            throw new InvalidDataException($"Contact field {fieldName} is invalid or exceeds its limit.");
    }

    private static string NormalizeContactId(string contactId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contactId);
        var normalized = contactId.Trim().ToLowerInvariant();
        if (!Guid.TryParseExact(normalized, "N", out _))
            throw new InvalidDataException("Contact identifier is invalid.");
        return normalized;
    }

    private static void ValidateSha256(string value, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value, fieldName);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"{fieldName} must be a SHA-256 value.");
    }

    private static void AppendFingerprintField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendFingerprintField(hash, bytes);
    }

    private static void AppendFingerprintField(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private const string Schema =
        """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=FULL;
        PRAGMA auto_vacuum=NONE;
        PRAGMA foreign_keys=ON;
        CREATE TABLE IF NOT EXISTS accounts(
            account_id TEXT PRIMARY KEY
        ) STRICT;
        CREATE TABLE IF NOT EXISTS account_profile_revisions(
            account_id TEXT NOT NULL REFERENCES accounts(account_id),
            revision INTEGER NOT NULL CHECK(revision > 0),
            display_name TEXT NOT NULL,
            receive_protocol TEXT NOT NULL CHECK(receive_protocol IN ('Imap','Pop3')),
            receive_host TEXT NOT NULL,
            receive_port INTEGER NOT NULL CHECK(receive_port BETWEEN 1 AND 65535),
            receive_tls_mode TEXT NOT NULL CHECK(receive_tls_mode IN ('ImplicitTls','RequiredStartTls')),
            smtp_host TEXT NOT NULL,
            smtp_port INTEGER NOT NULL CHECK(smtp_port BETWEEN 1 AND 65535),
            smtp_tls_mode TEXT NOT NULL CHECK(smtp_tls_mode IN ('ImplicitTls','RequiredStartTls')),
            credential_user_name TEXT NOT NULL,
            sender_address TEXT NOT NULL,
            saved_utc TEXT NOT NULL,
            PRIMARY KEY(account_id, revision)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS calendar_events(
            event_id TEXT PRIMARY KEY CHECK(length(event_id) = 32),
            created_utc TEXT NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS calendar_event_sources(
            event_id TEXT PRIMARY KEY REFERENCES calendar_events(event_id),
            source_kind TEXT NOT NULL CHECK(source_kind IN (
                'LocalManual','EmailSuggestion','ImportedIcs','Google','Apple')),
            evidence_id TEXT NULL UNIQUE CHECK(evidence_id IS NULL OR length(evidence_id) = 64),
            source_message_identity_sha256 TEXT NULL
                CHECK(source_message_identity_sha256 IS NULL OR length(source_message_identity_sha256) = 64),
            provider_account_id TEXT NULL CHECK(
                provider_account_id IS NULL OR length(provider_account_id) BETWEEN 1 AND 512),
            provider_event_id TEXT NULL CHECK(
                provider_event_id IS NULL OR length(provider_event_id) BETWEEN 1 AND 1024),
            provider_series_id TEXT NULL CHECK(
                provider_series_id IS NULL OR (
                    length(provider_series_id) = 64 AND
                    provider_series_id NOT GLOB '*[^0-9a-f]*')),
            created_utc TEXT NOT NULL,
            CHECK (
                (source_kind='EmailSuggestion' AND evidence_id IS NOT NULL
                    AND source_message_identity_sha256 IS NOT NULL
                    AND provider_account_id IS NULL AND provider_event_id IS NULL
                    AND provider_series_id IS NULL)
                OR
                (source_kind IN ('Google','Apple') AND evidence_id IS NULL
                    AND source_message_identity_sha256 IS NULL
                    AND provider_account_id IS NOT NULL AND provider_event_id IS NOT NULL)
                OR
                (source_kind IN ('LocalManual','ImportedIcs') AND evidence_id IS NULL
                    AND source_message_identity_sha256 IS NULL
                    AND provider_account_id IS NULL AND provider_event_id IS NULL
                    AND provider_series_id IS NULL)
            )
        ) STRICT;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_calendar_provider_event
        ON calendar_event_sources(source_kind, provider_account_id, provider_event_id)
        WHERE provider_account_id IS NOT NULL AND provider_event_id IS NOT NULL;
        CREATE TABLE IF NOT EXISTS calendar_provider_observations(
            source_kind TEXT NOT NULL CHECK(source_kind IN ('Google','Apple')),
            provider_account_identity_sha256 TEXT NOT NULL CHECK(
                length(provider_account_identity_sha256) = 64),
            provider_event_identity_sha256 TEXT NOT NULL CHECK(
                length(provider_event_identity_sha256) = 64),
            provider_revision_identity_sha256 TEXT NOT NULL CHECK(
                length(provider_revision_identity_sha256) = 64),
            payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
            event_id TEXT NULL REFERENCES calendar_events(event_id),
            outcome TEXT NOT NULL CHECK(outcome IN (
                'Added','Updated','Unchanged','Tombstoned','AlreadyTombstoned',
                'MissingDeletionRetained','ResurrectionConflictRetained')),
            observed_utc TEXT NOT NULL,
            provider_series_identity_sha256 TEXT NULL CHECK(
                provider_series_identity_sha256 IS NULL OR (
                    length(provider_series_identity_sha256) = 64 AND
                    provider_series_identity_sha256 NOT GLOB '*[^0-9a-f]*')),
            PRIMARY KEY(
                source_kind, provider_account_identity_sha256,
                provider_event_identity_sha256, provider_revision_identity_sha256)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS calendar_provider_series_snapshots(
            source_kind TEXT NOT NULL CHECK(source_kind IN ('Google','Apple')),
            provider_account_identity_sha256 TEXT NOT NULL CHECK(
                length(provider_account_identity_sha256) = 64 AND
                provider_account_identity_sha256 NOT GLOB '*[^0-9a-f]*'),
            provider_series_identity_sha256 TEXT NOT NULL CHECK(
                length(provider_series_identity_sha256) = 64 AND
                provider_series_identity_sha256 NOT GLOB '*[^0-9a-f]*'),
            provider_series_revision_identity_sha256 TEXT NOT NULL CHECK(
                length(provider_series_revision_identity_sha256) = 64 AND
                provider_series_revision_identity_sha256 NOT GLOB '*[^0-9a-f]*'),
            payload_sha256 TEXT NOT NULL CHECK(
                length(payload_sha256) = 64 AND payload_sha256 NOT GLOB '*[^0-9a-f]*'),
            observed_utc TEXT NOT NULL,
            PRIMARY KEY(
                source_kind, provider_account_identity_sha256,
                provider_series_identity_sha256,
                provider_series_revision_identity_sha256)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS calendar_provider_series_snapshot_members(
            source_kind TEXT NOT NULL,
            provider_account_identity_sha256 TEXT NOT NULL,
            provider_series_identity_sha256 TEXT NOT NULL,
            provider_series_revision_identity_sha256 TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 499),
            provider_event_identity_sha256 TEXT NOT NULL CHECK(
                length(provider_event_identity_sha256) = 64 AND
                provider_event_identity_sha256 NOT GLOB '*[^0-9a-f]*'),
            PRIMARY KEY(
                source_kind, provider_account_identity_sha256,
                provider_series_identity_sha256,
                provider_series_revision_identity_sha256, ordinal),
            UNIQUE(
                source_kind, provider_account_identity_sha256,
                provider_series_identity_sha256,
                provider_series_revision_identity_sha256,
                provider_event_identity_sha256),
            FOREIGN KEY(
                source_kind, provider_account_identity_sha256,
                provider_series_identity_sha256,
                provider_series_revision_identity_sha256)
            REFERENCES calendar_provider_series_snapshots(
                source_kind, provider_account_identity_sha256,
                provider_series_identity_sha256,
                provider_series_revision_identity_sha256)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS calendar_event_revisions(
            event_id TEXT NOT NULL REFERENCES calendar_events(event_id),
            revision INTEGER NOT NULL CHECK(revision > 0),
            title TEXT NOT NULL CHECK(
                length(title) BETWEEN 1 AND 160 AND instr(title, char(0)) = 0),
            start_local TEXT NOT NULL CHECK(length(start_local) = 27),
            end_local TEXT NOT NULL CHECK(length(end_local) = 27 AND end_local > start_local),
            all_day INTEGER NOT NULL CHECK(all_day IN (0,1)),
            location TEXT NOT NULL CHECK(
                length(location) <= 512 AND instr(location, char(0)) = 0),
            notes TEXT NOT NULL CHECK(
                length(notes) <= 16384 AND instr(notes, char(0)) = 0),
            is_tombstoned INTEGER NOT NULL CHECK(is_tombstoned IN (0,1)),
            saved_utc TEXT NOT NULL,
            PRIMARY KEY(event_id, revision)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_calendar_event_revisions_agenda
        ON calendar_event_revisions(start_local, end_local, event_id, revision);
        CREATE TABLE IF NOT EXISTS contacts(
            contact_id TEXT PRIMARY KEY CHECK(length(contact_id) = 32),
            created_utc TEXT NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS contact_revisions(
            contact_id TEXT NOT NULL REFERENCES contacts(contact_id),
            revision INTEGER NOT NULL CHECK(revision > 0),
            display_name TEXT NOT NULL,
            email_address TEXT NOT NULL,
            notes TEXT NOT NULL,
            is_group INTEGER NOT NULL CHECK(is_group IN (0,1)),
            is_tombstoned INTEGER NOT NULL CHECK(is_tombstoned IN (0,1)),
            saved_utc TEXT NOT NULL,
            PRIMARY KEY(contact_id, revision)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS contact_revision_group_members(
            contact_id TEXT NOT NULL,
            revision INTEGER NOT NULL,
            ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
            member_kind TEXT NOT NULL CHECK(member_kind IN ('EmailAddress','FriendlyName','NestedGroup')),
            member_value TEXT NOT NULL,
            PRIMARY KEY(contact_id, revision, ordinal),
            FOREIGN KEY(contact_id, revision) REFERENCES contact_revisions(contact_id, revision)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS folders(
            folder_id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(account_id),
            parent_folder_id TEXT NULL REFERENCES folders(folder_id),
            display_name TEXT NOT NULL,
            is_sent_items INTEGER NOT NULL DEFAULT 0 CHECK(is_sent_items IN (0,1)),
            is_deleted_items INTEGER NOT NULL DEFAULT 0 CHECK(is_deleted_items IN (0,1)),
            remote_full_name TEXT NULL,
            is_local_only INTEGER NOT NULL DEFAULT 1 CHECK(is_local_only IN (0,1)),
            is_tombstoned INTEGER NOT NULL DEFAULT 0 CHECK(is_tombstoned IN (0,1)),
            CHECK ((is_local_only=1 AND remote_full_name IS NULL) OR
                   (is_local_only=0 AND remote_full_name IS NOT NULL))
        ) STRICT;
        CREATE TABLE IF NOT EXISTS local_folder_revisions(
            folder_id TEXT NOT NULL REFERENCES folders(folder_id),
            revision INTEGER NOT NULL CHECK(revision > 0),
            display_name TEXT NOT NULL,
            is_tombstoned INTEGER NOT NULL CHECK(is_tombstoned IN (0,1)),
            saved_utc TEXT NOT NULL,
            PRIMARY KEY(folder_id, revision)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS local_folder_hierarchy_revisions(
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            folder_id TEXT NOT NULL REFERENCES folders(folder_id),
            parent_folder_id TEXT NULL REFERENCES folders(folder_id),
            saved_utc TEXT NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS content_blobs(
            sha256 TEXT PRIMARY KEY CHECK(length(sha256) = 64),
            byte_count INTEGER NOT NULL CHECK(byte_count >= 0),
            relative_path TEXT NOT NULL UNIQUE,
            content_kind TEXT NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS messages(
            message_id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(account_id),
            identity_key TEXT NOT NULL,
            imap_uid TEXT NULL,
            imap_uid_validity TEXT NULL,
            pop_uidl TEXT NULL,
            rfc_message_id TEXT NULL,
            subject TEXT NOT NULL,
            raw_mime_sha256 TEXT NOT NULL REFERENCES content_blobs(sha256),
            raw_mime_bytes INTEGER NOT NULL CHECK(raw_mime_bytes >= 0),
            imported_utc TEXT NOT NULL,
            is_tombstoned INTEGER NOT NULL DEFAULT 0 CHECK(is_tombstoned IN (0,1)),
            tombstoned_utc TEXT NULL,
            UNIQUE(account_id, identity_key)
        ) STRICT;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_pop_uidl
        ON messages(account_id, pop_uidl)
        WHERE pop_uidl IS NOT NULL;
        CREATE TABLE IF NOT EXISTS folder_messages(
            folder_id TEXT NOT NULL REFERENCES folders(folder_id),
            message_id TEXT NOT NULL REFERENCES messages(message_id),
            is_tombstoned INTEGER NOT NULL DEFAULT 0 CHECK(is_tombstoned IN (0,1)),
            PRIMARY KEY(folder_id, message_id)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS normalized_message_bodies(
            message_id TEXT PRIMARY KEY REFERENCES messages(message_id),
            sender TEXT NOT NULL,
            recipients TEXT NOT NULL,
            sent_utc TEXT NULL,
            text_body TEXT NULL,
            sanitizable_html_body TEXT NULL
        ) STRICT;
        CREATE VIRTUAL TABLE IF NOT EXISTS message_search USING fts5(
            message_id UNINDEXED,
            subject,
            sender,
            recipients,
            text_body,
            tokenize='unicode61 remove_diacritics 2'
        );
        INSERT INTO message_search(message_id, subject, sender, recipients, text_body)
        SELECT m.message_id, m.subject, b.sender, b.recipients, b.text_body
        FROM messages m
        JOIN normalized_message_bodies b ON b.message_id = m.message_id
        WHERE NOT EXISTS (
            SELECT 1 FROM message_search s WHERE s.message_id = m.message_id
        );
        CREATE TABLE IF NOT EXISTS attachments(
            attachment_id TEXT PRIMARY KEY,
            message_id TEXT NOT NULL REFERENCES messages(message_id),
            ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
            file_name TEXT NOT NULL,
            media_type TEXT NOT NULL,
            byte_count INTEGER NOT NULL CHECK(byte_count >= 0),
            content_sha256 TEXT NOT NULL REFERENCES content_blobs(sha256),
            content_id TEXT NOT NULL DEFAULT '',
            UNIQUE(message_id, ordinal)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS message_flags(
            message_id TEXT NOT NULL REFERENCES messages(message_id),
            flag TEXT NOT NULL,
            PRIMARY KEY(message_id, flag)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS folder_message_flags(
            folder_id TEXT NOT NULL,
            message_id TEXT NOT NULL,
            flag TEXT NOT NULL,
            PRIMARY KEY(folder_id, message_id, flag),
            FOREIGN KEY(folder_id, message_id) REFERENCES folder_messages(folder_id, message_id)
        ) STRICT;
        INSERT OR IGNORE INTO folder_message_flags(folder_id, message_id, flag)
        SELECT fm.folder_id, mf.message_id, mf.flag
        FROM message_flags mf
        JOIN folder_messages fm ON fm.message_id = mf.message_id;
        CREATE TABLE IF NOT EXISTS message_remote_identities(
            account_id TEXT NOT NULL REFERENCES accounts(account_id),
            folder_id TEXT NOT NULL REFERENCES folders(folder_id),
            uid_validity TEXT NOT NULL,
            uid TEXT NOT NULL,
            message_id TEXT NOT NULL REFERENCES messages(message_id),
            PRIMARY KEY(account_id, folder_id, uid_validity, uid)
        ) STRICT;
        INSERT OR IGNORE INTO message_remote_identities(account_id, folder_id, uid_validity, uid, message_id)
        SELECT m.account_id, fm.folder_id, m.imap_uid_validity, m.imap_uid, m.message_id
        FROM messages m
        JOIN folder_messages fm ON fm.message_id = m.message_id
        WHERE m.imap_uid_validity IS NOT NULL AND m.imap_uid IS NOT NULL;
        CREATE TABLE IF NOT EXISTS sync_cursors(
            account_id TEXT NOT NULL REFERENCES accounts(account_id),
            folder_id TEXT NOT NULL REFERENCES folders(folder_id),
            uid_validity TEXT NOT NULL,
            highest_uid INTEGER NOT NULL CHECK(highest_uid >= 0),
            updated_utc TEXT NOT NULL,
            PRIMARY KEY(account_id, folder_id)
        ) STRICT;
        CREATE TABLE IF NOT EXISTS pending_operations(
            operation_id TEXT PRIMARY KEY,
            idempotency_key TEXT NOT NULL UNIQUE,
            operation_type TEXT NOT NULL,
            payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
            created_utc TEXT NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS operation_journal(
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            operation_id TEXT NOT NULL REFERENCES pending_operations(operation_id),
            state TEXT NOT NULL,
            event_utc TEXT NOT NULL,
            server_acknowledgement TEXT NULL,
            error_code TEXT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS outbound_messages(
            operation_id TEXT PRIMARY KEY REFERENCES pending_operations(operation_id),
            account_id TEXT NULL REFERENCES accounts(account_id),
            message_id TEXT NOT NULL UNIQUE,
            raw_mime_sha256 TEXT NOT NULL REFERENCES content_blobs(sha256),
            raw_mime_bytes INTEGER NOT NULL CHECK(raw_mime_bytes > 0),
            envelope_sender TEXT NOT NULL DEFAULT '',
            envelope_recipients TEXT NOT NULL DEFAULT ''
        ) STRICT;
        CREATE TABLE IF NOT EXISTS sent_outbound_projections(
            operation_id TEXT PRIMARY KEY REFERENCES outbound_messages(operation_id),
            message_id TEXT NOT NULL UNIQUE REFERENCES messages(message_id),
            projected_utc TEXT NOT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS mailbox_mutation_operations(
            operation_id TEXT PRIMARY KEY REFERENCES pending_operations(operation_id),
            account_id TEXT NOT NULL REFERENCES accounts(account_id),
            message_id TEXT NOT NULL REFERENCES messages(message_id),
            source_folder_id TEXT NOT NULL REFERENCES folders(folder_id),
            mutation_kind TEXT NOT NULL CHECK(mutation_kind IN (
                'MoveToFolder','SetFlag','ClearFlag','MoveToDeletedItems','RetainDeletedHistory')),
            target_folder_id TEXT NULL REFERENCES folders(folder_id),
            flag_name TEXT NULL,
            expected_uid_validity TEXT NULL,
            expected_uid TEXT NULL,
            CHECK (
                (mutation_kind IN ('MoveToFolder','MoveToDeletedItems') AND target_folder_id IS NOT NULL AND flag_name IS NULL)
                OR
                (mutation_kind IN ('SetFlag','ClearFlag') AND target_folder_id IS NULL AND flag_name IS NOT NULL)
                OR
                (mutation_kind='RetainDeletedHistory' AND target_folder_id IS NULL AND flag_name IS NULL)
            )
        ) STRICT;
        CREATE TABLE IF NOT EXISTS drafts(
            draft_id TEXT PRIMARY KEY CHECK(length(draft_id) = 32),
            created_utc TEXT NOT NULL,
            is_tombstoned INTEGER NOT NULL DEFAULT 0 CHECK(is_tombstoned IN (0,1)),
            tombstoned_utc TEXT NULL
        ) STRICT;
        CREATE TABLE IF NOT EXISTS draft_revisions(
            draft_id TEXT NOT NULL REFERENCES drafts(draft_id),
            revision INTEGER NOT NULL CHECK(revision > 0),
            raw_mime_sha256 TEXT NOT NULL REFERENCES content_blobs(sha256),
            raw_mime_bytes INTEGER NOT NULL CHECK(raw_mime_bytes > 0),
            subject TEXT NOT NULL,
            sender TEXT NOT NULL,
            recipients TEXT NOT NULL,
            body_snippet TEXT NOT NULL,
            saved_utc TEXT NOT NULL,
            PRIMARY KEY(draft_id, revision)
        ) STRICT;
        CREATE TRIGGER IF NOT EXISTS no_delete_messages
        BEFORE DELETE ON messages BEGIN SELECT RAISE(ABORT, 'physical message purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_message_history
        BEFORE UPDATE OF message_id, account_id, identity_key, imap_uid, imap_uid_validity, pop_uidl,
            rfc_message_id, subject, raw_mime_sha256, raw_mime_bytes, imported_utc
        ON messages BEGIN SELECT RAISE(ABORT, 'message history is immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_message_untombstone
        BEFORE UPDATE OF is_tombstoned ON messages
        WHEN NEW.is_tombstoned < OLD.is_tombstoned
        BEGIN SELECT RAISE(ABORT, 'message tombstones cannot be removed'); END;
        CREATE TRIGGER IF NOT EXISTS no_delete_folders
        BEFORE DELETE ON folders BEGIN SELECT RAISE(ABORT, 'physical folder purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS no_delete_local_folder_revisions
        BEFORE DELETE ON local_folder_revisions
        BEGIN SELECT RAISE(ABORT, 'physical local folder revision purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS no_update_local_folder_revisions
        BEFORE UPDATE ON local_folder_revisions
        BEGIN SELECT RAISE(ABORT, 'local folder revisions are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_delete_local_folder_hierarchy_revisions
        BEFORE DELETE ON local_folder_hierarchy_revisions
        BEGIN SELECT RAISE(ABORT, 'physical local folder hierarchy revision purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS no_update_local_folder_hierarchy_revisions
        BEFORE UPDATE ON local_folder_hierarchy_revisions
        BEGIN SELECT RAISE(ABORT, 'local folder hierarchy revisions are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_local_folder_untombstone
        BEFORE UPDATE OF is_tombstoned ON folders
        WHEN OLD.is_tombstoned=1 AND NEW.is_tombstoned=0
             AND EXISTS (SELECT 1 FROM local_folder_revisions r WHERE r.folder_id=OLD.folder_id)
        BEGIN SELECT RAISE(ABORT, 'local folder tombstones cannot be removed'); END;
        CREATE TRIGGER IF NOT EXISTS require_local_folder_revision_for_metadata_update
        BEFORE UPDATE OF display_name, is_tombstoned ON folders
        WHEN EXISTS (SELECT 1 FROM local_folder_revisions r WHERE r.folder_id=OLD.folder_id)
             AND NOT (OLD.is_tombstoned=1 AND NEW.is_tombstoned=0)
             AND NOT EXISTS (
                 SELECT 1 FROM local_folder_revisions latest
                 WHERE latest.folder_id=OLD.folder_id
                   AND latest.display_name=NEW.display_name
                   AND latest.is_tombstoned=NEW.is_tombstoned
                   AND latest.revision=(
                       SELECT MAX(candidate.revision)
                       FROM local_folder_revisions candidate
                       WHERE candidate.folder_id=OLD.folder_id))
        BEGIN SELECT RAISE(ABORT, 'local folder metadata changes require an immutable revision'); END;
        CREATE TRIGGER IF NOT EXISTS require_local_folder_hierarchy_revision_for_parent_update
        BEFORE UPDATE OF parent_folder_id ON folders
        WHEN EXISTS (SELECT 1 FROM local_folder_revisions r WHERE r.folder_id=OLD.folder_id)
             AND NOT EXISTS (
                 SELECT 1 FROM local_folder_hierarchy_revisions latest
                 WHERE latest.folder_id=OLD.folder_id
                   AND latest.parent_folder_id IS NEW.parent_folder_id
                   AND latest.sequence=(
                       SELECT MAX(candidate.sequence)
                       FROM local_folder_hierarchy_revisions candidate
                       WHERE candidate.folder_id=OLD.folder_id))
        BEGIN SELECT RAISE(ABORT, 'local folder parent changes require an immutable hierarchy revision'); END;
        CREATE TRIGGER IF NOT EXISTS no_delete_folder_messages
        BEFORE DELETE ON folder_messages BEGIN SELECT RAISE(ABORT, 'physical folder-message history purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_folder_message_identity
        BEFORE UPDATE OF folder_id, message_id ON folder_messages
        BEGIN SELECT RAISE(ABORT, 'folder-message identity is immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_delete_content
        BEFORE DELETE ON content_blobs BEGIN SELECT RAISE(ABORT, 'physical content purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS no_update_content
        BEFORE UPDATE ON content_blobs BEGIN SELECT RAISE(ABORT, 'content-addressed records are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_delete_attachments
        BEFORE DELETE ON attachments BEGIN SELECT RAISE(ABORT, 'physical attachment purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_attachment_history
        BEFORE UPDATE OF attachment_id, message_id, ordinal, file_name, media_type, byte_count, content_sha256
        ON attachments BEGIN SELECT RAISE(ABORT, 'attachment history is immutable'); END;
        CREATE TRIGGER IF NOT EXISTS one_time_attachment_content_id_enrichment
        BEFORE UPDATE OF content_id ON attachments
        WHEN OLD.content_id <> '' AND NEW.content_id <> OLD.content_id
        BEGIN SELECT RAISE(ABORT, 'attachment Content-ID enrichment cannot be rewritten'); END;
        CREATE TRIGGER IF NOT EXISTS no_delete_normalized_message_bodies
        BEFORE DELETE ON normalized_message_bodies
        BEGIN SELECT RAISE(ABORT, 'normalized message history cannot be deleted'); END;
        CREATE TRIGGER IF NOT EXISTS additive_normalized_message_bodies
        BEFORE UPDATE ON normalized_message_bodies
        WHEN (OLD.sender <> '' AND NEW.sender IS NOT OLD.sender)
          OR (OLD.recipients <> '' AND NEW.recipients IS NOT OLD.recipients)
          OR (OLD.sent_utc IS NOT NULL AND NEW.sent_utc IS NOT OLD.sent_utc)
          OR (OLD.text_body IS NOT NULL AND NEW.text_body IS NOT OLD.text_body)
          OR (OLD.sanitizable_html_body IS NOT NULL AND NEW.sanitizable_html_body IS NOT OLD.sanitizable_html_body)
        BEGIN SELECT RAISE(ABORT, 'normalized message history can only be enriched once'); END;
        CREATE TRIGGER IF NOT EXISTS no_delete_remote_identities
        BEFORE DELETE ON message_remote_identities
        BEGIN SELECT RAISE(ABORT, 'remote message identity history cannot be deleted'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_remote_identity_mapping
        BEFORE UPDATE OF account_id, folder_id, uid_validity, uid, message_id ON message_remote_identities
        WHEN NEW.account_id IS NOT OLD.account_id OR NEW.folder_id IS NOT OLD.folder_id
          OR NEW.uid_validity IS NOT OLD.uid_validity OR NEW.uid IS NOT OLD.uid
          OR NEW.message_id IS NOT OLD.message_id
        BEGIN SELECT RAISE(ABORT, 'remote message identity history is immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_delete_accounts
        BEFORE DELETE ON accounts BEGIN SELECT RAISE(ABORT, 'account history cannot be deleted'); END;
        CREATE TRIGGER IF NOT EXISTS no_update_accounts
        BEFORE UPDATE ON accounts BEGIN SELECT RAISE(ABORT, 'account identity is immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_delete_drafts
        BEFORE DELETE ON drafts BEGIN SELECT RAISE(ABORT, 'physical draft purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS no_draft_untombstone
        BEFORE UPDATE OF is_tombstoned ON drafts
        WHEN NEW.is_tombstoned < OLD.is_tombstoned
        BEGIN SELECT RAISE(ABORT, 'draft tombstones cannot be removed'); END;
        CREATE TRIGGER IF NOT EXISTS no_update_draft_revisions
        BEFORE UPDATE ON draft_revisions BEGIN SELECT RAISE(ABORT, 'draft revisions are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_delete_draft_revisions
        BEFORE DELETE ON draft_revisions BEGIN SELECT RAISE(ABORT, 'physical draft revision purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS append_only_operation_journal_update
        BEFORE UPDATE ON operation_journal BEGIN SELECT RAISE(ABORT, 'operation journal is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS append_only_operation_journal_delete
        BEFORE DELETE ON operation_journal BEGIN SELECT RAISE(ABORT, 'operation journal is append-only'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_pending_operations_update
        BEFORE UPDATE ON pending_operations BEGIN SELECT RAISE(ABORT, 'pending operations are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_pending_operations_delete
        BEFORE DELETE ON pending_operations BEGIN SELECT RAISE(ABORT, 'pending operations cannot be deleted'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_outbound_messages_update
        BEFORE UPDATE ON outbound_messages BEGIN SELECT RAISE(ABORT, 'outbound MIME records are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_outbound_messages_delete
        BEFORE DELETE ON outbound_messages BEGIN SELECT RAISE(ABORT, 'outbound MIME records cannot be deleted'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_sent_outbound_projections_update
        BEFORE UPDATE ON sent_outbound_projections
        BEGIN SELECT RAISE(ABORT, 'Sent Items outbound projections are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_sent_outbound_projections_delete
        BEFORE DELETE ON sent_outbound_projections
        BEGIN SELECT RAISE(ABORT, 'Sent Items outbound projections cannot be deleted'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_mailbox_mutations_update
        BEFORE UPDATE ON mailbox_mutation_operations BEGIN SELECT RAISE(ABORT, 'mailbox mutation records are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_mailbox_mutations_delete
        BEFORE DELETE ON mailbox_mutation_operations BEGIN SELECT RAISE(ABORT, 'mailbox mutation records cannot be deleted'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_account_profile_revisions_update
        BEFORE UPDATE ON account_profile_revisions BEGIN SELECT RAISE(ABORT, 'mail account profile revisions are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_account_profile_revisions_delete
        BEFORE DELETE ON account_profile_revisions BEGIN SELECT RAISE(ABORT, 'mail account profile revisions cannot be deleted'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_calendar_events_update
        BEFORE UPDATE ON calendar_events BEGIN SELECT RAISE(ABORT, 'calendar event identity is immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_calendar_events_delete
        BEFORE DELETE ON calendar_events BEGIN SELECT RAISE(ABORT, 'physical calendar event purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_calendar_event_sources_update
        BEFORE UPDATE ON calendar_event_sources BEGIN SELECT RAISE(ABORT, 'calendar event source provenance is immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_calendar_event_sources_delete
        BEFORE DELETE ON calendar_event_sources BEGIN SELECT RAISE(ABORT, 'calendar event source provenance cannot be deleted'); END;
        CREATE TRIGGER IF NOT EXISTS enforce_calendar_event_revision_sequence
        BEFORE INSERT ON calendar_event_revisions
        WHEN NEW.revision <> COALESCE((
            SELECT MAX(existing.revision) + 1
            FROM calendar_event_revisions existing
            WHERE existing.event_id=NEW.event_id), 1)
        BEGIN SELECT RAISE(ABORT, 'calendar event revisions must be contiguous and append-only'); END;
        CREATE TRIGGER IF NOT EXISTS no_calendar_event_resurrection
        BEFORE INSERT ON calendar_event_revisions
        WHEN EXISTS (
            SELECT 1 FROM calendar_event_revisions latest
            WHERE latest.event_id=NEW.event_id AND latest.is_tombstoned=1
              AND latest.revision=(
                  SELECT MAX(candidate.revision)
                  FROM calendar_event_revisions candidate
                  WHERE candidate.event_id=NEW.event_id))
        BEGIN SELECT RAISE(ABORT, 'calendar event tombstones cannot be removed'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_calendar_event_revisions_update
        BEFORE UPDATE ON calendar_event_revisions BEGIN SELECT RAISE(ABORT, 'calendar event revisions are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_calendar_event_revisions_delete
        BEFORE DELETE ON calendar_event_revisions BEGIN SELECT RAISE(ABORT, 'physical calendar event revision purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_calendar_provider_observations_update
        BEFORE UPDATE ON calendar_provider_observations BEGIN SELECT RAISE(ABORT, 'calendar provider observations are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_calendar_provider_observations_delete
        BEFORE DELETE ON calendar_provider_observations BEGIN SELECT RAISE(ABORT, 'calendar provider observation purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_calendar_provider_series_snapshots_update
        BEFORE UPDATE ON calendar_provider_series_snapshots BEGIN SELECT RAISE(ABORT, 'calendar provider series snapshots are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_calendar_provider_series_snapshots_delete
        BEFORE DELETE ON calendar_provider_series_snapshots BEGIN SELECT RAISE(ABORT, 'calendar provider series snapshot purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_calendar_provider_series_members_update
        BEFORE UPDATE ON calendar_provider_series_snapshot_members BEGIN SELECT RAISE(ABORT, 'calendar provider series membership is immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_calendar_provider_series_members_delete
        BEFORE DELETE ON calendar_provider_series_snapshot_members BEGIN SELECT RAISE(ABORT, 'calendar provider series membership purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_contacts_update
        BEFORE UPDATE ON contacts BEGIN SELECT RAISE(ABORT, 'contact identity is immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_contacts_delete
        BEFORE DELETE ON contacts BEGIN SELECT RAISE(ABORT, 'physical contact purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_contact_revisions_update
        BEFORE UPDATE ON contact_revisions BEGIN SELECT RAISE(ABORT, 'contact revisions are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_contact_revisions_delete
        BEFORE DELETE ON contact_revisions BEGIN SELECT RAISE(ABORT, 'physical contact revision purge is disabled'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_contact_group_members_update
        BEFORE UPDATE ON contact_revision_group_members BEGIN SELECT RAISE(ABORT, 'contact group revisions are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_contact_group_members_delete
        BEFORE DELETE ON contact_revision_group_members BEGIN SELECT RAISE(ABORT, 'physical contact group history purge is disabled'); END;
        PRAGMA trusted_schema=OFF;
        """;
}
