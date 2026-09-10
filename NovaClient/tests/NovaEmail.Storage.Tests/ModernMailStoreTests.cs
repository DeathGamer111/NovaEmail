using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Data.Sqlite;
using NovaEmail.Safety;

namespace NovaEmail.Storage.Tests;

public sealed class ModernMailStoreTests
{
    [Fact]
    public async Task FixtureRunStoreWritesOnlyToItsOpaqueFixedChild()
    {
        if (!OperatingSystem.IsWindows()) return;
        var testRoot = CreateTestRoot();
        var marker = Path.Combine(testRoot, ".novaemail-fixture-run.json");
        await File.WriteAllTextAsync(marker, "{}", CancellationToken.None);
        try
        {
            var readBoundary = ApprovedReadOnlyRoot.AuthorizeExisting(testRoot);
            var approvedStore = ApprovedFixtureStoreRoot.AuthorizeFixedChild(readBoundary);
            Directory.CreateDirectory(approvedStore.ApprovedDataRoot);
            ProtectForCurrentUser(approvedStore.ApprovedDataRoot);
            var store = ModernMailStore.CreateFixtureRunStore(approvedStore);

            await store.InitializeAsync(CancellationToken.None);

            Assert.Equal(
                Path.Combine(testRoot, ".novaemail-test-store"),
                store.ApprovedDataRoot);
            Assert.True(File.Exists(Path.Combine(
                approvedStore.ApprovedDataRoot, "novaemail.db")));
            Assert.True(File.Exists(marker));
            Assert.Single(Directory.GetDirectories(testRoot));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FixtureRunStoreNeverCreatesAnUnprotectedDestination()
    {
        if (!OperatingSystem.IsWindows()) return;
        var testRoot = CreateTestRoot();
        var marker = Path.Combine(testRoot, ".novaemail-fixture-run.json");
        await File.WriteAllTextAsync(marker, "{}", CancellationToken.None);
        try
        {
            var readBoundary = ApprovedReadOnlyRoot.AuthorizeExisting(testRoot);
            var approvedStore = ApprovedFixtureStoreRoot.AuthorizeFixedChild(readBoundary);

            var error = Assert.Throws<DirectoryNotFoundException>(() =>
                ModernMailStore.CreateFixtureRunStore(approvedStore));

            Assert.Contains("pre-created", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(approvedStore.ApprovedDataRoot));
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FixtureRunStoreRejectsAnExistingInheritedAclBeforeWriting()
    {
        if (!OperatingSystem.IsWindows()) return;
        var testRoot = CreateTestRoot();
        var marker = Path.Combine(testRoot, ".novaemail-fixture-run.json");
        await File.WriteAllTextAsync(marker, "{}", CancellationToken.None);
        try
        {
            var readBoundary = ApprovedReadOnlyRoot.AuthorizeExisting(testRoot);
            var approvedStore = ApprovedFixtureStoreRoot.AuthorizeFixedChild(readBoundary);
            Directory.CreateDirectory(approvedStore.ApprovedDataRoot);

            var error = Assert.Throws<NovaEmail.Safety.SecurityException>(() =>
                ModernMailStore.CreateFixtureRunStore(approvedStore));

            Assert.Contains("ACL gate", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(
                approvedStore.ApprovedDataRoot, "novaemail.db")));
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void ExistingDatabaseHardLinkIsRejectedBeforeSQLiteCanWrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        var testRoot = CreateTestRoot();
        try
        {
            var protectedFile = Path.Combine(testRoot, "must-not-change.db");
            var protectedBytes = "not actually sqlite; must remain exact"u8.ToArray();
            File.WriteAllBytes(protectedFile, protectedBytes);
            var dataRoot = Path.Combine(testRoot, "data");
            Directory.CreateDirectory(dataRoot);
            var database = Path.Combine(dataRoot, "novaemail.db");
            CreateFileHardLink(database, protectedFile);

            var error = Assert.Throws<NovaEmail.Safety.SecurityException>(() =>
                new ModernMailStore(database, dataRoot));

            Assert.Contains("hard-linked", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(protectedBytes, File.ReadAllBytes(protectedFile));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OperationJournalEnforcesIdempotencyAndAppendOnlyHistory()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var database = Path.Combine(testRoot, "data", "novaemail.db");
            var store = new ModernMailStore(database, Path.Combine(testRoot, "data"));
            await store.InitializeAsync();
            var hash = new string('a', 64);
            var first = await store.RegisterOperationAsync("send-42", "Send", hash);
            var second = await store.RegisterOperationAsync("send-42", "Send", hash);
            await store.AppendOperationEventAsync(first.OperationId, OperationEventState.Queued);
            await store.AppendOperationEventAsync(first.OperationId, OperationEventState.Acknowledged, "smtp-ok");

            Assert.False(first.WasAlreadyPresent);
            Assert.True(second.WasAlreadyPresent);
            Assert.Equal(first.OperationId, second.OperationId);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.RegisterOperationAsync("send-42", "Send", new string('b', 64)));

            await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE operation_journal SET state = 'Rewritten';";
            var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("append-only", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OfflineOutboxRetainsExactMimeAndExposesLatestJournalStateWithoutRewriting()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            var rawMime =
                "From: author@example.test\r\nTo: recipient@example.test\r\n" +
                "Message-ID: <offline-queue@example.test>\r\nSubject: Immutable queue\r\n\r\nExact outbound bytes.\r\n";
            var bytes = System.Text.Encoding.ASCII.GetBytes(rawMime);

            var first = await store.QueueOutboundMessageAsync(
                "account-a", "draft-revision:draft-a:1:hash-a", "offline-queue@example.test", bytes);
            var repeated = await store.QueueOutboundMessageAsync(
                "account-a", "draft-revision:draft-a:1:hash-a", "offline-queue@example.test", bytes);

            Assert.Equal(first.OperationId, repeated.OperationId);
            Assert.Equal(first.IdempotencyKey, repeated.IdempotencyKey);
            Assert.Equal("account-a", first.AccountId);
            Assert.Equal(first.AccountId, repeated.AccountId);
            Assert.Equal(first.MessageId, repeated.MessageId);
            Assert.Equal(first.RawMimeSha256, repeated.RawMimeSha256);
            Assert.Equal(first.RawMimeBytes, repeated.RawMimeBytes);
            Assert.Equal(first.EnvelopeSender, repeated.EnvelopeSender);
            Assert.Equal(first.EnvelopeRecipients, repeated.EnvelopeRecipients);
            Assert.Equal(bytes, await store.ReadOutboundMimeAsync(first.OperationId));
            var queued = Assert.Single(await store.ReadOutboundOperationSummariesAsync());
            Assert.Equal(OperationEventState.Queued, queued.State);
            Assert.Equal("account-a", queued.AccountId);
            Assert.Equal(first.RawMimeSha256, queued.RawMimeSha256);
            Assert.Equal(bytes.LongLength, queued.RawMimeBytes);
            Assert.Single(await store.ReadReadyOutboundMessagesAsync("account-a", 10));
            Assert.Empty(await store.ReadReadyOutboundMessagesAsync("account-b", 10));

            await store.AppendOperationEventAsync(
                first.OperationId, OperationEventState.Conflict, errorCode: "SubmissionOutcomeUnknown");
            var conflict = Assert.Single(await store.ReadOutboundOperationSummariesAsync());
            Assert.Equal(OperationEventState.Conflict, conflict.State);
            Assert.Equal("SubmissionOutcomeUnknown", conflict.ErrorCode);
            Assert.Empty(await store.ReadReadyOutboundMessagesAsync("account-a", 10));
            Assert.Equal(bytes, await store.ReadOutboundMimeAsync(first.OperationId));

            await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE outbound_messages SET message_id = 'rewritten';";
            var updateError = await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());
            Assert.Contains("immutable", updateError.Message, StringComparison.Ordinal);
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM pending_operations;";
            var deleteError = await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
            Assert.Contains("cannot be deleted", deleteError.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LocalOutboxStagingIdentityRetainsMimeButCanNeverEnterDispatchReadySet()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            var bytes = System.Text.Encoding.ASCII.GetBytes(
                "From: author@example.test\r\nTo: recipient@example.test\r\n" +
                "Message-ID: <staged-only@example.test>\r\nSubject: Staged only\r\n\r\n" +
                "This message must remain local.\r\n");

            var queued = await store.QueueOutboundMessageAsync(
                MailAccountProfilePolicy.LocalOutboxStagingAccountId,
                "draft-revision:staged-only:1:hash-a",
                "staged-only@example.test",
                bytes);

            Assert.Equal(bytes, await store.ReadOutboundMimeAsync(queued.OperationId));
            Assert.Single(await store.ReadOutboundOperationSummariesAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ReadReadyOutboundMessagesAsync(
                    MailAccountProfilePolicy.LocalOutboxStagingAccountId, 1));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void DraftAndOutboundMimeLengthBoundaryRejectsEmptyAndOversizedContent()
    {
        ModernMailStore.ValidateRawMimeLength(ModernMailStore.MaximumRawMimeBytes, "MIME");
        Assert.Throws<InvalidDataException>(() =>
            ModernMailStore.ValidateRawMimeLength(0, "MIME"));
        Assert.Throws<InvalidDataException>(() =>
            ModernMailStore.ValidateRawMimeLength(
                ModernMailStore.MaximumRawMimeBytes + 1, "MIME"));
    }

    [Fact]
    public async Task DraftAndOutboundReadsRejectSameLengthContentAddressedTampering()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            var outboundBytes =
                "From: sender@example.test\r\nTo: receiver@example.test\r\n" +
                "Message-ID: <tamper-outbound@example.test>\r\n\r\nOutbound original.\r\n";
            var draftBytes =
                "From: sender@example.test\r\nTo: receiver@example.test\r\n" +
                "Message-ID: <tamper-draft@example.test>\r\n\r\nDraft original.\r\n";
            var queued = await store.QueueOutboundMessageAsync(
                "account-a", "tamper-outbound", "tamper-outbound@example.test",
                System.Text.Encoding.ASCII.GetBytes(outboundBytes));
            var draft = await store.SaveDraftRevisionAsync(new DraftRevisionImport(
                Guid.NewGuid().ToString("N"), System.Text.Encoding.ASCII.GetBytes(draftBytes),
                "Draft", "sender@example.test", "receiver@example.test", "Draft original."));

            await using var connection = new SqliteConnection(
                $"Data Source={database};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            async Task<string> ReadPathAsync(string sha256)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT relative_path FROM content_blobs WHERE sha256 = $sha256;";
                command.Parameters.AddWithValue("$sha256", sha256);
                var relative = (string?)await command.ExecuteScalarAsync() ??
                    throw new InvalidOperationException("Test content path was not found.");
                return Path.Combine(dataRoot, "content", relative);
            }

            var outboundPath = await ReadPathAsync(queued.RawMimeSha256);
            var draftPath = await ReadPathAsync(draft.RawMimeSha256);
            var outboundTampered = System.Text.Encoding.ASCII.GetBytes(outboundBytes);
            outboundTampered[^3] ^= 0x01;
            var draftTampered = System.Text.Encoding.ASCII.GetBytes(draftBytes);
            draftTampered[^3] ^= 0x01;
            await File.WriteAllBytesAsync(outboundPath, outboundTampered);
            await File.WriteAllBytesAsync(draftPath, draftTampered);

            var outboundError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReadOutboundMimeAsync(queued.OperationId));
            var draftError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReadDraftMimeAsync(draft.DraftId, draft.Revision));
            Assert.Contains("SHA-256", outboundError.Message, StringComparison.Ordinal);
            Assert.Contains("SHA-256", draftError.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MailAccountProfilesAllowConfiguredHostsAndRemainAppendOnly()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var database = Path.Combine(testRoot, "data", "novaemail.db");
            var store = new ModernMailStore(database, Path.Combine(testRoot, "data"));
            await store.InitializeAsync();
            var input = new MailAccountProfileInput(
                "account-local", "Primary account", "Imap", "203.0.113.10", 993,
                "ImplicitTls", "198.51.100.10", 465, "ImplicitTls", "login-name",
                "sender@novaemail.test");

            var first = await store.SaveMailAccountProfileRevisionAsync(input);
            var second = await store.SaveMailAccountProfileRevisionAsync(
                input with { DisplayName = "Primary account revised", ReceiveTlsMode = "RequiredStartTls" });
            var latest = Assert.Single(await store.ReadLatestMailAccountProfilesAsync());

            Assert.Equal(1, first.Revision);
            Assert.Equal(2, second.Revision);
            Assert.Equal(second, latest);
            Assert.Equal("sender@novaemail.test", latest.SenderAddress);
            var publicProfile = await store.SaveMailAccountProfileRevisionAsync(
                input with { AccountId = "public", ReceiveHost = "8.8.8.8" });
            Assert.Equal("8.8.8.8", publicProfile.ReceiveHost);

            await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE account_profile_revisions SET display_name = 'rewritten';";
            var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("immutable", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingAccountProfilesReceiveAdditiveSenderAddressWithoutLosingRevisions()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            var first = await store.SaveMailAccountProfileRevisionAsync(
                new MailAccountProfileInput(
                    "account-additive", "Before schema upgrade", "Imap", "localhost", 3993,
                    "ImplicitTls", "localhost", 3465, "ImplicitTls", "login",
                    "before@example.test"));
            await using (var connection = new SqliteConnection(
                             $"Data Source={database};Mode=ReadWrite;Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "ALTER TABLE account_profile_revisions DROP COLUMN sender_address;";
                await command.ExecuteNonQueryAsync();
            }

            var upgraded = new ModernMailStore(database, dataRoot);
            await upgraded.InitializeAsync();
            var retained = Assert.Single(await upgraded.ReadLatestMailAccountProfilesAsync());
            Assert.Equal(first.AccountId, retained.AccountId);
            Assert.Equal(first.Revision, retained.Revision);
            Assert.Equal(first.DisplayName, retained.DisplayName);
            Assert.Equal(string.Empty, retained.SenderAddress);
            var appended = await upgraded.SaveMailAccountProfileRevisionAsync(
                new MailAccountProfileInput(
                    first.AccountId, "After schema upgrade", "Imap", "localhost", 3993,
                    "ImplicitTls", "localhost", 3465, "ImplicitTls", "login",
                    "after@example.test"));
            Assert.Equal(2, appended.Revision);
            Assert.Equal("after@example.test", appended.SenderAddress);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void DatabaseOutsideApprovedRootIsRejected()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var approved = Path.Combine(testRoot, "approved");
            Directory.CreateDirectory(approved);
            Assert.Throws<InvalidOperationException>(() =>
                new ModernMailStore(Path.Combine(testRoot, "outside.db"), approved));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ReceivedAttachmentsAreContentAddressedIdempotentAndHashVerified()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            var rawMime =
                "From: sender@example.test\r\nTo: recipient@example.test\r\n" +
                "Message-ID: <received-attachment@example.test>\r\nSubject: Attachment\r\n\r\nExact MIME.\r\n";
            var attachmentBytes = "decoded received attachment"u8.ToArray();
            var import = new ReceivedMessageImport(
                "account-a", "inbox", "71", "19", null,
                "<received-attachment@example.test>", "Attachment",
                "sender@example.test", "recipient@example.test", null,
                System.Text.Encoding.ASCII.GetBytes(rawMime), "Exact MIME.", null, ["\\Seen"],
                [new MessageAttachmentImport(
                    0, "evidence.txt", "text/plain", attachmentBytes, "inline-evidence@example.test")]);

            var first = await store.StoreReceivedMessageAsync(import);
            var repeated = await store.StoreReceivedMessageAsync(import);
            var summary = Assert.Single(await store.ReadStoredAttachmentSummariesAsync(first.MessageId));

            Assert.False(first.WasAlreadyPresent);
            Assert.True(repeated.WasAlreadyPresent);
            Assert.Equal(first.MessageId, repeated.MessageId);
            Assert.Equal("evidence.txt", summary.FileName);
            Assert.Equal("text/plain", summary.MediaType);
            Assert.Equal(attachmentBytes.LongLength, summary.ByteCount);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(attachmentBytes)), summary.ContentSha256);
            Assert.Equal("inline-evidence@example.test", summary.ContentId);
            Assert.Equal(attachmentBytes, await store.ReadStoredAttachmentContentAsync(first.MessageId, 0));
            Assert.Equal(1, await store.GetAttachmentCountAsync());

            await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT b.relative_path FROM attachments a JOIN content_blobs b " +
                "ON b.sha256=a.content_sha256 WHERE a.message_id=$messageId AND a.ordinal=0;";
            command.Parameters.AddWithValue("$messageId", first.MessageId);
            var relativePath = Assert.IsType<string>(await command.ExecuteScalarAsync());
            var contentPath = Path.Combine(dataRoot, "content", relativePath);
            await File.WriteAllBytesAsync(contentPath, "tampered"u8.ToArray());

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReadStoredAttachmentContentAsync(first.MessageId, 0));
            Assert.Contains("immutable record", error.Message, StringComparison.Ordinal);

            var reimportError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.StoreReceivedMessageAsync(import));
            Assert.Contains("content-addressed data", reimportError.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptContentCatalogPathBlocksIdempotentReimport()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var rawMime = "From: sender@example.test\r\nSubject: Catalog\r\n\r\nExact bytes.\r\n"u8.ToArray();
            var import = new ReceivedMessageImport(
                "account-a", "inbox", "71", "20", null, "<catalog@example.test>",
                "Catalog", "sender@example.test", "recipient@example.test", null,
                rawMime, "Exact bytes.", null, [], []);
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            var first = await store.StoreReceivedMessageAsync(import);
            var contentPath = Path.Combine(
                dataRoot, "content", first.RawMimeSha256[..2], first.RawMimeSha256 + ".mime");
            var contentHash = SHA256.HashData(await File.ReadAllBytesAsync(contentPath));

            await using (var connection = new SqliteConnection(
                             $"Data Source={database};Mode=ReadWrite;Pooling=False"))
            {
                await connection.OpenAsync();
                await using var corrupt = connection.CreateCommand();
                corrupt.CommandText =
                    "DROP TRIGGER no_update_content; " +
                    "UPDATE content_blobs SET relative_path='wrong-path.mime' WHERE sha256=$sha256;";
                corrupt.Parameters.AddWithValue("$sha256", first.RawMimeSha256);
                await corrupt.ExecuteNonQueryAsync();
            }

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.StoreReceivedMessageAsync(import));

            Assert.Contains("catalog path", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(contentHash, SHA256.HashData(await File.ReadAllBytesAsync(contentPath)));
            Assert.Equal(1, await store.GetMessageCountAsync());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingAttachmentSchemaReceivesAdditiveContentIdWithoutLosingBytes()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            var attachmentBytes = "preserved pre-content-id attachment"u8.ToArray();
            var stored = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account", "inbox", "9", "7", null, "<pre-content-id@example.test>",
                "Pre Content-ID", "sender@example.test", "receiver@example.test", null,
                "Subject: Pre Content-ID\r\n\r\nbody\r\n"u8.ToArray(), "body", null, [],
                [new MessageAttachmentImport(0, "preserved.bin", "application/octet-stream", attachmentBytes)]));
            await using (var connection = new SqliteConnection(
                             $"Data Source={database};Mode=ReadWrite;Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "DROP TRIGGER one_time_attachment_content_id_enrichment; " +
                    "ALTER TABLE attachments DROP COLUMN content_id;";
                await command.ExecuteNonQueryAsync();
            }

            var upgraded = new ModernMailStore(database, dataRoot);
            await upgraded.InitializeAsync();
            var summary = Assert.Single(
                await upgraded.ReadStoredAttachmentSummariesAsync(stored.MessageId));
            Assert.Equal(string.Empty, summary.ContentId);
            Assert.Equal(attachmentBytes,
                await upgraded.ReadStoredAttachmentContentAsync(stored.MessageId, summary.Ordinal));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProtectedMailboxPagingIsBoundedExactAndExcludesTombstones()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            var stored = new List<(StoredMessageResult Result, byte[] RawMime)>();
            for (var index = 0; index < 3; ++index)
            {
                var folderId = index == 2 ? "archive" : "inbox";
                var rawMime = System.Text.Encoding.ASCII.GetBytes(
                    $"From: sender{index}@example.test\r\nTo: recipient@example.test\r\n" +
                    $"Subject: Protected page {index}\r\nMessage-ID: <protected-{index}@example.test>\r\n\r\n" +
                    $"Shared paging token body {index}.\r\n");
                var result = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                    "account-a", folderId, "42", (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), null,
                    $"<protected-{index}@example.test>", $"Protected page {index}",
                    $"sender{index}@example.test", "recipient@example.test",
                    new DateTimeOffset(2026, 2, 3, 4, 5, index, TimeSpan.Zero), rawMime,
                    $"Shared paging token body {index}.", null, []));
                stored.Add((result, rawMime));
            }

            var first = await store.ListStoredMessagesPageAsync(2);
            Assert.Equal(2, first.Messages.Count);
            Assert.Equal(2, first.NextOffset);
            var second = await store.ListStoredMessagesPageAsync(2, first.NextOffset!.Value);
            Assert.Single(second.Messages);
            Assert.Null(second.NextOffset);
            Assert.Equal(3, first.Messages.Concat(second.Messages).Select(message => message.MessageId).Distinct().Count());

            var folders = await store.ReadStoredFolderSummariesAsync();
            Assert.Equal(2, folders.Count);
            Assert.Equal(1, folders.Single(item => item.FolderId == "archive").ActiveMessageCount);
            Assert.Equal(2, folders.Single(item => item.FolderId == "inbox").ActiveMessageCount);
            var inbox = await store.ListStoredFolderMessagesPageAsync("inbox", 2);
            Assert.Equal(2, inbox.Messages.Count);
            Assert.All(inbox.Messages, message => Assert.Equal("inbox", message.FolderId));
            Assert.Null(inbox.NextOffset);

            var searchFirst = await store.SearchStoredMessagesPageAsync("shared paging token", 2);
            Assert.Equal(2, searchFirst.Messages.Count);
            Assert.Equal(2, searchFirst.NextOffset);
            var searchSecond = await store.SearchStoredMessagesPageAsync(
                "shared paging token", 2, searchFirst.NextOffset!.Value);
            Assert.Single(searchSecond.Messages);
            Assert.Null(searchSecond.NextOffset);
            Assert.Single((await store.SearchStoredFolderMessagesPageAsync(
                "archive", "shared paging token", 10)).Messages);

            var selected = first.Messages[0];
            var exactMime = await store.ReadStoredMimeAsync(selected.MessageId);
            Assert.Equal(selected.RawMimeBytes, exactMime.LongLength);
            Assert.Equal(selected.RawMimeSha256, Convert.ToHexStringLower(SHA256.HashData(exactMime)));
            Assert.Equal(stored.Single(item => item.Result.MessageId == selected.MessageId).RawMime, exactMime);

            await store.MarkMessageDeletedAsync(stored[0].Result.MessageId);
            var visible = await store.ListStoredMessagesPageAsync(10);
            Assert.Equal(2, visible.Messages.Count);
            Assert.DoesNotContain(visible.Messages, message => message.MessageId == stored[0].Result.MessageId);
            folders = await store.ReadStoredFolderSummariesAsync();
            Assert.Equal(1, folders.Single(item => item.FolderId == "inbox").ActiveMessageCount);
            Assert.Equal(1, folders.Single(item => item.FolderId == "inbox").TombstonedMessageCount);
            Assert.Equal(
                stored[0].RawMime,
                await store.ReadStoredMimeAsync(stored[0].Result.MessageId));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                store.ListStoredMessagesPageAsync(0));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                store.ListStoredMessagesPageAsync(251));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                store.ListStoredMessagesPageAsync(10, -1));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AccountAndFolderPagingAndSearchNeverLeakAcrossAccounts()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            for (var index = 0; index < 2; index++)
            {
                var raw = System.Text.Encoding.ASCII.GetBytes(
                    $"From: alpha{index}@example.test\r\nTo: recipient@example.test\r\n" +
                    $"Subject: Alpha scoped {index}\r\nMessage-ID: <alpha-scoped-{index}@example.test>\r\n\r\n" +
                    $"Account alpha search token {index}.\r\n");
                await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                    "account-alpha", "alpha-inbox",
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture), "100", null,
                    $"<alpha-scoped-{index}@example.test>", $"Alpha scoped {index}",
                    $"alpha{index}@example.test", "recipient@example.test", null,
                    raw, $"Account alpha search token {index}.", null, []));
            }
            var betaRaw = "From: beta@example.test\r\nTo: recipient@example.test\r\nSubject: Beta scoped\r\nMessage-ID: <beta-scoped@example.test>\r\n\r\nAccount beta search token.\r\n"u8.ToArray();
            await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account-beta", "beta-inbox", "1", "200", null,
                "<beta-scoped@example.test>", "Beta scoped", "beta@example.test",
                "recipient@example.test", null, betaRaw, "Account beta search token.", null, []));

            var alphaFirst = await store.ListStoredAccountMessagesPageAsync("account-alpha", 1);
            Assert.Single(alphaFirst.Messages);
            Assert.Equal(1, alphaFirst.NextOffset);
            var alphaSecond = await store.ListStoredAccountMessagesPageAsync(
                "account-alpha", 1, alphaFirst.NextOffset!.Value);
            Assert.Single(alphaSecond.Messages);
            Assert.Null(alphaSecond.NextOffset);
            Assert.All(alphaFirst.Messages.Concat(alphaSecond.Messages),
                message => Assert.Equal("alpha-inbox", message.FolderId));

            var beta = await store.ListStoredAccountMessagesPageAsync("account-beta", 10);
            Assert.Single(beta.Messages);
            Assert.Equal("beta-inbox", beta.Messages[0].FolderId);
            Assert.Equal(2, (await store.SearchStoredAccountMessagesPageAsync(
                "account-alpha", "account search token", 10)).Messages.Count);
            Assert.Single((await store.SearchStoredAccountMessagesPageAsync(
                "account-beta", "account search token", 10)).Messages);
            Assert.Empty((await store.SearchStoredAccountMessagesPageAsync(
                "account-beta", "alpha", 10)).Messages);
            Assert.Equal(2, (await store.SearchStoredFolderMessagesPageAsync(
                "alpha-inbox", "account search token", 10)).Messages.Count);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ListStoredAccountMessagesPageAsync("bad\naccount", 10));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BackupAndRestoreNeverOverwriteExistingDestinations()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            var backupRoot = Path.Combine(testRoot, "backup");
            await ModernMailBackupService.CreateAsync(store, backupRoot);

            await Assert.ThrowsAsync<IOException>(() =>
                ModernMailBackupService.CreateAsync(store, backupRoot));
            var existingRestore = Path.Combine(testRoot, "existing-restore");
            Directory.CreateDirectory(existingRestore);
            var marker = Path.Combine(existingRestore, "preserve.txt");
            await File.WriteAllTextAsync(marker, "preserve");
            await Assert.ThrowsAsync<IOException>(() =>
                ModernMailBackupService.RestoreAsync(backupRoot, existingRestore));
            Assert.Equal("preserve", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BackupRejectsCorruptCatalogedContentWithoutPublishingOrChangingTheSource()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account-a", "inbox", "3", "9", null, "<backup-corrupt@example.test>",
                "Backup corruption", "sender@example.test", "recipient@example.test", null,
                "Subject: Backup corruption\r\n\r\nExact source.\r\n"u8.ToArray(),
                "Exact source.", null, []));
            var contentPath = Assert.Single(Directory.GetFiles(
                Path.Combine(dataRoot, "content"), "*", SearchOption.AllDirectories));
            await File.AppendAllTextAsync(contentPath, "tampered");
            var sourceHashBefore = await HashTreeAsync(dataRoot);
            var backupRoot = Path.Combine(testRoot, "backup");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ModernMailBackupService.CreateAsync(store, backupRoot));

            Assert.False(Directory.Exists(backupRoot));
            var retainedPartial = Assert.Single(Directory.GetDirectories(
                testRoot, "backup.partial.*", SearchOption.TopDirectoryOnly));
            var relativePartial = Path.GetRelativePath(testRoot, retainedPartial);
            Assert.False(Path.IsPathRooted(relativePartial));
            Assert.False(relativePartial.Equals("..", StringComparison.Ordinal));
            Assert.False(relativePartial.StartsWith(
                ".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Assert.StartsWith(
                "backup.partial.", Path.GetFileName(retainedPartial), StringComparison.Ordinal);
            Assert.Equal(
                FileAttributes.Directory,
                File.GetAttributes(retainedPartial) &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint));
            Assert.True(new NovaEmail.Safety.CurrentUserOnlyAclProbe()
                .Inspect(retainedPartial).Passed);
            Assert.Equal(sourceHashBefore, await HashTreeAsync(dataRoot));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BackupAndRestorePreserveOfflineMailboxMutationJournalProjectionAndExactMime()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            var raw = "From: sender@example.test\r\nSubject: Restored filing\r\n\r\nRetain every byte.\r\n"u8.ToArray();
            var message = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account-a", "inbox", "31", "17", null, "<restored-filing@example.test>",
                "Restored filing", "sender@example.test", "receiver@example.test", null,
                raw, "Retain every byte.", null, ["\\Seen"]));
            await store.EnsureLocalFolderAsync(
                "account-a", "local-parent", "Local parent", null, isDeletedItems: false);
            await store.EnsureLocalFolderAsync(
                "account-a", "local-archive", "Local archive", "local-parent", isDeletedItems: false);
            await store.RenameLocalFolderAsync(
                "account-a", "local-archive", "Renamed local archive");
            var operation = await store.QueueMailboxMutationAsync(
                "backup-move-filing-1",
                new OfflineMailboxMutationIntent(
                    "account-a", message.MessageId, "inbox", MailboxMutationKind.MoveToFolder,
                    TargetFolderId: "local-archive"));

            var backup = await ModernMailBackupService.CreateAsync(store, Path.Combine(testRoot, "backup"));
            var restored = await ModernMailBackupService.RestoreAsync(
                backup.BackupRoot, Path.Combine(testRoot, "restored"));
            var restoredStore = new ModernMailStore(restored.RestoredDatabasePath, restored.RestoredDataRoot);

            Assert.Empty((await restoredStore.ListStoredFolderMessagesPageAsync("inbox", 10)).Messages);
            var restoredMessage = Assert.Single(
                (await restoredStore.ListStoredFolderMessagesPageAsync("local-archive", 10)).Messages);
            Assert.Equal(message.MessageId, restoredMessage.MessageId);
            Assert.Equal(raw, await restoredStore.ReadStoredMimeAsync(message.MessageId));
            Assert.Equal(1, await restoredStore.GetMessageCountAsync());
            var restoredOperation = Assert.Single(await restoredStore.ReadMailboxMutationOperationsAsync());
            Assert.Equal(operation.OperationId, restoredOperation.OperationId);
            Assert.Equal(operation.IdempotencyKey, restoredOperation.IdempotencyKey);
            Assert.Equal(MailboxMutationKind.MoveToFolder, restoredOperation.Kind);
            Assert.Equal(OperationEventState.Queued, restoredOperation.State);
            var restoredFolderHistory = await restoredStore.ReadLocalFolderRevisionHistoryAsync("local-archive");
            Assert.Equal(2, restoredFolderHistory.Count);
            Assert.Equal("Local archive", restoredFolderHistory[0].DisplayName);
            Assert.Equal("Renamed local archive", restoredFolderHistory[1].DisplayName);
            var restoredHierarchyHistory = await restoredStore.ReadLocalFolderHierarchyHistoryAsync("local-archive");
            var initialLocation = Assert.Single(restoredHierarchyHistory);
            Assert.Equal("local-parent", initialLocation.ParentFolderId);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRejectsUnknownManifestKindsBeforeCreatingAStagingStore()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            var backup = await ModernMailBackupService.CreateAsync(store, Path.Combine(testRoot, "backup"));
            var manifestPath = Path.Combine(backup.BackupRoot, "backup-manifest.json");
            var originalManifest = await File.ReadAllTextAsync(manifestPath);
            var hostileManifest = originalManifest.Replace(
                "\"kind\": \"sqlite\"", "\"kind\": \"unexpected\"", StringComparison.Ordinal);
            Assert.NotEqual(originalManifest, hostileManifest);
            await File.WriteAllTextAsync(manifestPath, hostileManifest);
            var restoreRoot = Path.Combine(testRoot, "restore");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ModernMailBackupService.RestoreAsync(backup.BackupRoot, restoreRoot));

            Assert.False(Directory.Exists(restoreRoot));
            Assert.Empty(Directory.GetDirectories(testRoot, "restore.partial.*"));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EmailCalendarSuggestionIsExplicitIdempotentAndSourceIdentityIsHashed()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            const string sourceIdentity = "<sensitive-calendar-source@example.test>";
            var evidenceId = new string('a', 64);
            var input = new LocalCalendarEventRevisionInput(
                EventId: null,
                Title: "Review quarterly schedule",
                StartLocal: new DateTime(2027, 3, 14, 9, 30, 0, DateTimeKind.Unspecified),
                EndLocal: new DateTime(2027, 3, 14, 10, 30, 0, DateTimeKind.Unspecified),
                AllDay: false,
                Location: string.Empty,
                Notes: "Explicitly confirmed suggestion.",
                SourceKind: CalendarEventSourceKind.EmailSuggestion,
                EvidenceId: evidenceId,
                SourceMessageIdentity: sourceIdentity);

            var first = await store.SaveLocalCalendarEventRevisionAsync(input);
            var repeated = await store.SaveLocalCalendarEventRevisionAsync(input);

            Assert.False(first.WasAlreadyPresent);
            Assert.True(repeated.WasAlreadyPresent);
            Assert.Equal(first.Event, repeated.Event);
            Assert.Equal(
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceIdentity))),
                first.Event.SourceMessageIdentitySha256);
            Assert.Equal(evidenceId, first.Event.EvidenceId);
            var agenda = await store.ReadLocalCalendarAgendaAsync(
                new DateTime(2027, 3, 14, 0, 0, 0, DateTimeKind.Unspecified),
                new DateTime(2027, 3, 15, 0, 0, 0, DateTimeKind.Unspecified));
            Assert.Equal(first.Event, Assert.Single(agenda));

            foreach (var persistedFile in Directory.GetFiles(dataRoot, "*", SearchOption.AllDirectories))
            {
                var persistedText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(persistedFile));
                Assert.DoesNotContain(sourceIdentity, persistedText, StringComparison.Ordinal);
            }
            var conflictingInput = input with { SourceMessageIdentity = "<different@example.test>" };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveLocalCalendarEventRevisionAsync(conflictingInput));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CalendarRevisionsAndTombstonesAreAppendOnlyAndCannotBePurgedOrResurrected()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            var first = await store.SaveLocalCalendarEventRevisionAsync(
                new LocalCalendarEventRevisionInput(
                    null,
                    "First title",
                    new DateTime(2027, 5, 1, 12, 0, 0, DateTimeKind.Unspecified),
                    new DateTime(2027, 5, 1, 13, 0, 0, DateTimeKind.Unspecified),
                    false,
                    string.Empty,
                    string.Empty,
                    CalendarEventSourceKind.LocalManual));
            var second = await store.SaveLocalCalendarEventRevisionAsync(
                new LocalCalendarEventRevisionInput(
                    first.Event.EventId,
                    "Revised title",
                    first.Event.StartLocal,
                    first.Event.EndLocal.AddMinutes(30),
                    false,
                    "Conference room",
                    "The first revision remains retained.",
                    CalendarEventSourceKind.LocalManual));
            var tombstone = await store.TombstoneLocalCalendarEventAsync(first.Event.EventId);

            Assert.Equal(1, first.Event.Revision);
            Assert.Equal(2, second.Event.Revision);
            Assert.Equal(3, tombstone.Revision);
            Assert.True(tombstone.IsTombstoned);
            var history = await store.ReadLocalCalendarEventRevisionHistoryAsync(first.Event.EventId);
            Assert.Equal([1, 2, 3], history.Select(item => item.Revision));
            Assert.Equal("First title", history[0].Title);
            Assert.Equal("Revised title", history[1].Title);
            Assert.Empty(await store.ReadLocalCalendarAgendaAsync(
                new DateTime(2027, 5, 1, 0, 0, 0, DateTimeKind.Unspecified),
                new DateTime(2027, 5, 2, 0, 0, 0, DateTimeKind.Unspecified)));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveLocalCalendarEventRevisionAsync(
                    new LocalCalendarEventRevisionInput(
                        first.Event.EventId,
                        "Resurrection attempt",
                        first.Event.StartLocal,
                        first.Event.EndLocal,
                        false,
                        string.Empty,
                        string.Empty,
                        CalendarEventSourceKind.LocalManual)));
            var assessment = await store.ReadNoPurgeCompactionAssessmentAsync();
            Assert.Equal(4, assessment.ImmutableRevisionCount);
            Assert.False(assessment.PhysicalPurgePermitted);
            Assert.Equal(0, assessment.ReclaimableBytes);

            await using var connection = new SqliteConnection(
                $"Data Source={database};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE calendar_event_revisions SET title='rewritten';";
            var updateError = await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());
            Assert.Contains("immutable", updateError.Message, StringComparison.OrdinalIgnoreCase);
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM calendar_event_revisions;";
            var deleteError = await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
            Assert.Contains("purge is disabled", deleteError.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CalendarEventInputRejectsAmbiguousTimeAndFalseProvenanceClaims()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            var valid = new LocalCalendarEventRevisionInput(
                null,
                "Bounded event",
                new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Unspecified),
                new DateTime(2027, 6, 2, 0, 0, 0, DateTimeKind.Unspecified),
                true,
                string.Empty,
                string.Empty,
                CalendarEventSourceKind.LocalManual);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveLocalCalendarEventRevisionAsync(valid with
                {
                    StartLocal = DateTime.SpecifyKind(valid.StartLocal, DateTimeKind.Local),
                }));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveLocalCalendarEventRevisionAsync(valid with
                {
                    EndLocal = valid.StartLocal.AddDays(367),
                }));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveLocalCalendarEventRevisionAsync(valid with
                {
                    StartLocal = valid.StartLocal.AddHours(1),
                    EndLocal = valid.EndLocal.AddHours(1),
                }));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveLocalCalendarEventRevisionAsync(valid with
                {
                    SourceKind = CalendarEventSourceKind.EmailSuggestion,
                    EvidenceId = new string('A', 64),
                    SourceMessageIdentity = "message-id",
                }));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveLocalCalendarEventRevisionAsync(valid with
                {
                    SourceKind = CalendarEventSourceKind.Google,
                    ProviderAccountId = "account",
                }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveLocalCalendarEventRevisionAsync(valid with
                {
                    SourceKind = CalendarEventSourceKind.Google,
                    ProviderAccountId = new string('a', 64),
                    ProviderEventId = new string('b', 64),
                }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveLocalCalendarEventRevisionAsync(valid with
                {
                    SourceKind = CalendarEventSourceKind.Google,
                    ProviderAccountId = new string('a', 64),
                    ProviderEventId = new string('b', 64),
                }));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveLocalCalendarEventRevisionAsync(valid with
                {
                    EvidenceId = new string('a', 64),
                    SourceMessageIdentity = "message-id",
                }));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveLocalCalendarEventRevisionAsync(valid with
                {
                    Title = "Control\u001bsequence",
                }));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DraftSavesCreateImmutableExactRevisionsAndDeletionOnlyTombstones()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            var firstBytes = "From: author@example.test\r\nTo: recipient@example.test\r\nSubject: First\r\n\r\nfirst body\r\n"u8.ToArray();
            var first = await store.SaveDraftRevisionAsync(new DraftRevisionImport(
                null, firstBytes, "First", "author@example.test", "recipient@example.test", "first body"));
            var secondBytes = "From: author@example.test\r\nTo: recipient@example.test\r\nSubject: Second\r\n\r\nsecond body\r\n"u8.ToArray();
            var second = await store.SaveDraftRevisionAsync(new DraftRevisionImport(
                first.DraftId, secondBytes, "Second", "author@example.test", "recipient@example.test", "second body"));

            Assert.Equal(1, first.Revision);
            Assert.Equal(2, second.Revision);
            Assert.Equal(firstBytes, await store.ReadDraftMimeAsync(first.DraftId, 1));
            Assert.Equal(secondBytes, await store.ReadDraftMimeAsync(first.DraftId, 2));
            var latest = Assert.Single(await store.ReadLatestDraftsAsync());
            Assert.Equal(second, latest);

            await store.TombstoneDraftAsync(first.DraftId);

            Assert.Empty(await store.ReadLatestDraftsAsync());
            Assert.Equal(firstBytes, await store.ReadDraftMimeAsync(first.DraftId, 1));
            Assert.Equal(secondBytes, await store.ReadDraftMimeAsync(first.DraftId, 2));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveDraftRevisionAsync(new DraftRevisionImport(
                    first.DraftId, secondBytes, "Blocked", "author@example.test", "recipient@example.test", "blocked")));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OfflineMoveIsIdempotentTombstoneBasedAndPreservesExactMimeAndJournal()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            var raw = "From: sender@example.test\r\nSubject: Offline filing\r\n\r\nPreserve me.\r\n"u8.ToArray();
            var message = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account-a", "inbox", "7", "11", null, "<filing@example.test>",
                "Offline filing", "sender@example.test", "receiver@example.test", null,
                raw, "Preserve me.", null, ["\\Seen"]));
            await store.SaveSyncCursorAsync(new StoredSyncCursor(
                "account-a", "inbox", "11", 7, DateTimeOffset.UtcNow));
            await store.EnsureLocalFolderAsync(
                "account-a", "local-archive", "Local archive", null, isDeletedItems: false);
            var intent = new OfflineMailboxMutationIntent(
                "account-a", message.MessageId, "inbox", MailboxMutationKind.MoveToFolder,
                TargetFolderId: "local-archive");

            var first = await store.QueueMailboxMutationAsync("move-filing-1", intent);
            var repeated = await store.QueueMailboxMutationAsync("move-filing-1", intent);

            Assert.False(first.WasAlreadyPresent);
            Assert.True(repeated.WasAlreadyPresent);
            Assert.Equal(first.OperationId, repeated.OperationId);
            Assert.Equal("11", first.ExpectedUidValidity);
            Assert.Equal(7UL, first.ExpectedUid);
            Assert.Empty((await store.ListStoredFolderMessagesPageAsync("inbox", 10)).Messages);
            Assert.Single((await store.ListStoredFolderMessagesPageAsync("local-archive", 10)).Messages);
            Assert.Equal(1, await store.GetMessageCountAsync());
            Assert.Equal(raw, await store.ReadStoredMimeAsync(message.MessageId));
            var operation = Assert.Single(await store.ReadMailboxMutationOperationsAsync());
            Assert.Equal(OperationEventState.Queued, operation.State);
            Assert.Equal(MailboxMutationKind.MoveToFolder, operation.Kind);

            await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM mailbox_mutation_operations;";
            var error = await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
            Assert.Contains("cannot be deleted", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OfflineFlagChangesAreJournaledAndReservedDeletionFlagIsRejected()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            var message = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account-a", "inbox", "8", "11", null, "<flags-offline@example.test>",
                "Offline flags", "sender@example.test", "receiver@example.test", null,
                "Subject: Offline flags\r\n\r\nbody\r\n"u8.ToArray(), "body", null, ["\\Seen"]));

            await store.QueueMailboxMutationAsync(
                "set-flag-1",
                new OfflineMailboxMutationIntent(
                    "account-a", message.MessageId, "inbox", MailboxMutationKind.SetFlag,
                    Flag: "\\Flagged"));
            await store.QueueMailboxMutationAsync(
                "clear-flag-1",
                new OfflineMailboxMutationIntent(
                    "account-a", message.MessageId, "inbox", MailboxMutationKind.ClearFlag,
                    Flag: "\\Seen"));

            var listed = Assert.Single((await store.ListStoredFolderMessagesPageAsync("inbox", 10)).Messages);
            Assert.Equal(["\\Flagged"], listed.Flags);
            Assert.Equal(2, (await store.ReadMailboxMutationOperationsAsync()).Count);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.QueueMailboxMutationAsync(
                "reserved-delete-flag",
                new OfflineMailboxMutationIntent(
                    "account-a", message.MessageId, "inbox", MailboxMutationKind.SetFlag,
                    Flag: "\\Deleted")));
            Assert.Equal(2, (await store.ReadMailboxMutationOperationsAsync()).Count);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DeletingFromDeletedItemsRetainsExactHistoryAndQueuesOnlyAMarker()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            await store.EnsureLocalFolderAsync(
                "account-a", "deleted-items", "Deleted Items", null, isDeletedItems: true);
            var raw = "Subject: Never purge me\r\n\r\nretained history\r\n"u8.ToArray();
            var message = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account-a", "deleted-items", "41", "22", null, "<retained-delete@example.test>",
                "Never purge me", "sender@example.test", "receiver@example.test", null,
                raw, "retained history", null, []));
            await store.SaveSyncCursorAsync(new StoredSyncCursor(
                "account-a", "deleted-items", "22", 41, DateTimeOffset.UtcNow));
            var intent = new OfflineMailboxMutationIntent(
                "account-a", message.MessageId, "deleted-items",
                MailboxMutationKind.RetainDeletedHistory);

            var queued = await store.QueueMailboxMutationAsync("retain-delete-1", intent);
            var repeated = await store.QueueMailboxMutationAsync("retain-delete-1", intent);

            Assert.Equal(MailboxMutationKind.RetainDeletedHistory, queued.Kind);
            Assert.Equal(OperationEventState.Queued, queued.State);
            Assert.Equal("22", queued.ExpectedUidValidity);
            Assert.Equal(41UL, queued.ExpectedUid);
            Assert.True(repeated.WasAlreadyPresent);
            Assert.Equal(queued.OperationId, repeated.OperationId);
            Assert.Empty((await store.ListStoredFolderMessagesPageAsync("deleted-items", 10)).Messages);
            Assert.Equal(1, await store.GetMessageCountAsync());
            Assert.Equal(raw, await store.ReadStoredMimeAsync(message.MessageId));
            var assessment = await store.ReadNoPurgeCompactionAssessmentAsync();
            Assert.Equal(1, assessment.TombstonedMessageCount);
            Assert.Equal(1, assessment.TombstonedFolderMessageLinkCount);
            Assert.Equal(0, assessment.ReclaimableBytes);
            Assert.False(assessment.PhysicalPurgePermitted);

            await using var connection = new SqliteConnection(
                $"Data Source={database};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            await using var purge = connection.CreateCommand();
            purge.CommandText = "DELETE FROM messages WHERE message_id=$messageId;";
            purge.Parameters.AddWithValue("$messageId", message.MessageId);
            var purgeError = await Assert.ThrowsAsync<SqliteException>(() => purge.ExecuteNonQueryAsync());
            Assert.Contains("physical message purge is disabled", purgeError.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RetainedHistoryDeletionRejectsMessagesOutsideDeletedItems()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            var message = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account-a", "inbox", null, null, "pop-retain-reject",
                "<retain-reject@example.test>", "Not deleted", "sender@example.test",
                "receiver@example.test", null,
                "Subject: Not deleted\r\n\r\nbody\r\n"u8.ToArray(), "body", null, []));

            await Assert.ThrowsAsync<InvalidDataException>(() => store.QueueMailboxMutationAsync(
                "retain-delete-invalid-source",
                new OfflineMailboxMutationIntent(
                    "account-a", message.MessageId, "inbox",
                    MailboxMutationKind.RetainDeletedHistory)));
            Assert.Single((await store.ListStoredFolderMessagesPageAsync("inbox", 10)).Messages);
            Assert.Equal(1, await store.GetMessageCountAsync());
            Assert.Empty(await store.ReadMailboxMutationOperationsAsync());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RetainedHistoryDeletionRejectsASecondActiveFolderLinkWithoutHidingIt()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            await store.EnsureLocalFolderAsync(
                "account-a", "deleted-items", "Deleted Items", null, isDeletedItems: true);
            await store.EnsureLocalFolderAsync(
                "account-a", "retained-copy", "Retained copy", null, isDeletedItems: false);
            var raw = "Subject: Shared retained history\r\n\r\nbody\r\n"u8.ToArray();
            var deleted = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account-a", "deleted-items", null, null, "shared-retained-pop",
                "<shared-retained@example.test>", "Shared retained history",
                "sender@example.test", "receiver@example.test", null, raw, "body", null, []));
            var copy = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account-a", "retained-copy", null, null, "shared-retained-pop",
                "<shared-retained@example.test>", "Shared retained history",
                "sender@example.test", "receiver@example.test", null, raw, "body", null, []));
            Assert.Equal(deleted.MessageId, copy.MessageId);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.QueueMailboxMutationAsync(
                    "retain-delete-shared-link",
                    new OfflineMailboxMutationIntent(
                        "account-a", deleted.MessageId, "deleted-items",
                        MailboxMutationKind.RetainDeletedHistory)));

            Assert.Contains("another active folder link", error.Message, StringComparison.Ordinal);
            Assert.Single((await store.ListStoredFolderMessagesPageAsync("deleted-items", 10)).Messages);
            Assert.Single((await store.ListStoredFolderMessagesPageAsync("retained-copy", 10)).Messages);
            Assert.Equal(raw, await store.ReadStoredMimeAsync(deleted.MessageId));
            Assert.Empty(await store.ReadMailboxMutationOperationsAsync());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LocalFolderRenameAndRetirementAppendImmutableRevisionsWithoutPurgingMail()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            await store.EnsureLocalFolderAsync(
                "account-a", "local-project", "Project", null, isDeletedItems: false);
            var raw = "Subject: Retained folder mail\r\n\r\nNever purge this.\r\n"u8.ToArray();
            var message = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account-a", "local-project", null, null, "local-folder-1",
                "<local-folder@example.test>", "Retained folder mail", "sender@example.test",
                "receiver@example.test", null, raw, "Never purge this.", null, []));

            var renamed = await store.RenameLocalFolderAsync(
                "account-a", "local-project", "Project archive");
            var duplicateRename = await store.RenameLocalFolderAsync(
                "account-a", "local-project", "Project archive");
            var retired = await store.TombstoneLocalFolderAsync("account-a", "local-project");
            var duplicateRetirement = await store.TombstoneLocalFolderAsync("account-a", "local-project");

            Assert.Equal(2, renamed.Revision);
            Assert.Equal(2, duplicateRename.Revision);
            Assert.Equal(3, retired.Revision);
            Assert.Equal(3, duplicateRetirement.Revision);
            var history = await store.ReadLocalFolderRevisionHistoryAsync("local-project");
            Assert.Equal(3, history.Count);
            Assert.Equal(["Project", "Project archive", "Project archive"],
                history.Select(item => item.DisplayName));
            Assert.Equal([false, false, true], history.Select(item => item.IsTombstoned));
            var summary = Assert.Single(await store.ReadStoredFolderSummariesAsync());
            Assert.Equal("Project archive", summary.DisplayName);
            Assert.True(summary.IsTombstoned);
            Assert.Equal(1, summary.ActiveMessageCount);
            Assert.Equal(raw, await store.ReadStoredMimeAsync(message.MessageId));
            Assert.Single((await store.ListStoredFolderMessagesPageAsync("local-project", 10)).Messages);

            await using var connection = new SqliteConnection(
                $"Data Source={database};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            async Task<SqliteException> RejectAsync(string sql)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                return await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            }
            Assert.Contains("revisions are immutable",
                (await RejectAsync(
                    "UPDATE local_folder_revisions SET display_name='rewritten' WHERE folder_id='local-project';"))
                .Message, StringComparison.Ordinal);
            Assert.Contains("revision purge is disabled",
                (await RejectAsync(
                    "DELETE FROM local_folder_revisions WHERE folder_id='local-project';"))
                .Message, StringComparison.Ordinal);
            Assert.Contains("tombstones cannot be removed",
                (await RejectAsync(
                    "UPDATE folders SET is_tombstoned=0 WHERE folder_id='local-project';"))
                .Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task NoPurgeCompactionAssessmentIsReadOnlyAndRetainsTombstonesAndHistory()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            var raw = System.Text.Encoding.ASCII.GetBytes(
                "From: sender@example.test\r\nTo: receiver@example.test\r\n" +
                "Subject: Assessment\r\nMessage-ID: <assessment@example.test>\r\n\r\nRetain me.\r\n");
            var stored = await store.StoreReceivedMessageAsync(new ReceivedMessageImport(
                "account", "inbox", "1", "1", null, "<assessment@example.test>",
                "Assessment", "sender@example.test", "receiver@example.test", null, raw,
                "Retain me.", null, [],
                [new MessageAttachmentImport(0, "retained.txt", "text/plain", "retained attachment"u8.ToArray())]));
            await store.MarkMessageDeletedAsync(stored.MessageId);
            await store.EnsureLocalFolderAsync(
                "account", "local-history", "Retained history", null, isDeletedItems: false);
            await store.RenameLocalFolderAsync("account", "local-history", "Retained history renamed");
            await store.TombstoneLocalFolderAsync("account", "local-history");

            var before = await HashSqliteDurableFilesAsync(database);
            var assessment = await store.ReadNoPurgeCompactionAssessmentAsync();
            var after = await HashSqliteDurableFilesAsync(database);

            Assert.Equal(before, after);
            Assert.Equal(1, assessment.MessageCount);
            Assert.Equal(1, assessment.TombstonedMessageCount);
            Assert.Equal(1, assessment.FolderMessageLinkCount);
            Assert.Equal(1, assessment.AttachmentCount);
            Assert.True(assessment.ContentBlobCount >= 2);
            Assert.True(assessment.ContentBlobBytes >= raw.Length + "retained attachment"u8.Length);
            Assert.Equal(1, assessment.TombstonedFolderCount);
            Assert.True(assessment.ImmutableRevisionCount >= 3);
            Assert.Equal(0, assessment.ReclaimableBytes);
            Assert.False(assessment.PhysicalPurgePermitted);
            Assert.Equal(raw, await store.ReadStoredMimeAsync(stored.MessageId));
            Assert.Equal(3, (await store.ReadLocalFolderRevisionHistoryAsync("local-history")).Count);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SimulatedFullDiskRollsBackMessageAndRestartStoresItExactlyOnce()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            long attemptedWriteBytes = 0;
            var store = new ModernMailStore(database, dataRoot, bytes =>
            {
                attemptedWriteBytes = bytes;
                throw new IOException("Simulated full disk at the content-addressed write boundary.");
            });
            await store.InitializeAsync();

            var rawMime = System.Text.Encoding.ASCII.GetBytes(
                "From: sender@example.test\r\nTo: receiver@example.test\r\n" +
                "Subject: Full disk recovery\r\nMessage-ID: <full-disk@example.test>\r\n\r\n" +
                new string('R', 256 * 1024));
            var import = new ReceivedMessageImport(
                "account-full", "inbox-full", "1", "1", null, "<full-disk@example.test>",
                "Full disk recovery", "sender@example.test", "receiver@example.test", null,
                rawMime, new string('R', 256 * 1024), null, []);
            var full = await Assert.ThrowsAsync<IOException>(() =>
                store.StoreReceivedMessageAsync(import));
            Assert.Contains("Simulated full disk", full.Message, StringComparison.Ordinal);
            Assert.Equal(rawMime.LongLength, attemptedWriteBytes);
            Assert.Equal(0, await store.GetMessageCountAsync());
            Assert.Empty(Directory.GetFiles(Path.Combine(dataRoot, "content"), "*", SearchOption.AllDirectories));
            var restarted = new ModernMailStore(database, dataRoot);
            await restarted.InitializeAsync();
            var recovered = await restarted.StoreReceivedMessageAsync(import);
            var repeated = await restarted.StoreReceivedMessageAsync(import);
            Assert.False(recovered.WasAlreadyPresent);
            Assert.True(repeated.WasAlreadyPresent);
            Assert.Equal(recovered.MessageId, repeated.MessageId);
            Assert.Equal(rawMime, await restarted.ReadStoredMimeAsync(recovered.MessageId));
            Assert.Equal(1, await restarted.GetMessageCountAsync());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string LowerSha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string CreateTestRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var root = Path.Combine(repositoryRoot, ".artifacts", "storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void ProtectForCurrentUser(string path)
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current SID unavailable in ACL test.");
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     currentUser,
                     new SecurityIdentifier("S-1-5-18"),
                     new SecurityIdentifier("S-1-5-32-544"),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void CreateFileHardLink(string linkPath, string targetPath)
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ??
            Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var process = Process.Start(new ProcessStartInfo(commandProcessor)
        {
            Arguments = $"/d /c mklink /H \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Unable to create the hard-link test fixture.");
        Assert.True(process.WaitForExit(10_000), "Hard-link creation timed out.");
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<IReadOnlyDictionary<string, string>> HashTreeAsync(string root)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            hashes.Add(relative, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }
        return hashes;
    }

    private static async Task<IReadOnlyDictionary<string, string>> HashSqliteDurableFilesAsync(string database)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in new[] { database, database + "-wal" })
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) continue;
            hashes.Add(Path.GetFileName(path),
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }
        return hashes;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "NovaClient")) &&
                Directory.Exists(Path.Combine(directory.FullName, "TestLab"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
