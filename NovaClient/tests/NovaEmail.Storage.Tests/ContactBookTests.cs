using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace NovaEmail.Storage.Tests;

public sealed class ContactBookTests
{
    [Fact]
    public async Task LocalContactEditsAndTombstonesCreateImmutableRevisionsWithoutPurgingHistory()
    {
        var root = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(root, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();

            var created = await store.SaveContactRevisionAsync(new ContactRevisionInput(
                null, "Austin Example", "austin@example.test", "Initial local note", false));
            var edited = await store.SaveContactRevisionAsync(new ContactRevisionInput(
                created.ContactId, "Austin Example", "austin.updated@example.test",
                "Updated without overwriting revision one", false));

            Assert.Equal(1, created.Revision);
            Assert.Equal(2, edited.Revision);
            Assert.Equal(created.ContactId, edited.ContactId);
            var history = await store.ReadContactRevisionHistoryAsync(created.ContactId);
            Assert.Equal(2, history.Count);
            Assert.Equal("austin@example.test", history[0].EmailAddress);
            Assert.Equal("austin.updated@example.test", history[1].EmailAddress);

            await store.TombstoneContactAsync(created.ContactId);
            Assert.Empty(await store.ReadLatestContactsAsync());
            var retained = Assert.Single(await store.ReadLatestContactsAsync(includeTombstoned: true));
            Assert.True(retained.IsTombstoned);
            Assert.Equal(3, retained.Revision);
            Assert.Equal(3, (await store.ReadContactRevisionHistoryAsync(created.ContactId)).Count);
            Assert.Equal(1, await store.GetContactCountAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveContactRevisionAsync(
                new ContactRevisionInput(created.ContactId, "Resurrected", "resurrected@example.test", "", false)));

            await using var connection = new SqliteConnection(
                $"Data Source={database};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            async Task<SqliteException> RejectAsync(string sql)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                return await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            }
            Assert.Contains("contact revisions are immutable",
                (await RejectAsync("UPDATE contact_revisions SET display_name='rewritten';")).Message,
                StringComparison.Ordinal);
            Assert.Contains("physical contact revision purge is disabled",
                (await RejectAsync("DELETE FROM contact_revisions;")).Message,
                StringComparison.Ordinal);
            Assert.Contains("physical contact purge is disabled",
                (await RejectAsync("DELETE FROM contacts;")).Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GroupsPreserveOrderedMembersAcrossEditAndTombstoneRevisions()
    {
        var root = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(root, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            ContactGroupMemberInput[] firstMembers =
            [
                new(ContactGroupMemberKind.EmailAddress, "one@example.test"),
                new(ContactGroupMemberKind.FriendlyName, "Second person"),
                new(ContactGroupMemberKind.NestedGroup, "Nested team"),
            ];
            var created = await store.SaveContactRevisionAsync(new ContactRevisionInput(
                null, "Modern team", "", "Offline group", true, firstMembers));
            var edited = await store.SaveContactRevisionAsync(new ContactRevisionInput(
                created.ContactId, "Modern team", "", "Second immutable revision", true,
                [.. firstMembers, new(ContactGroupMemberKind.EmailAddress, "four@example.test")]));

            Assert.Equal(firstMembers.Select(member => member.Value),
                (await store.ReadContactRevisionHistoryAsync(created.ContactId))[0].GroupMembers
                    .Select(member => member.Value));
            Assert.Equal(4, edited.GroupMembers.Count);
            await store.TombstoneContactAsync(created.ContactId);
            var history = await store.ReadContactRevisionHistoryAsync(created.ContactId);
            Assert.Equal(3, history.Count);
            Assert.Equal(edited.GroupMembers, history[2].GroupMembers);
            Assert.True(history[2].IsTombstoned);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecipientResolutionInventoryPagesBeyondUiLimitAndExcludesTombstones()
    {
        var root = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(root, "data");
            var database = Path.Combine(dataRoot, "novaemail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            await using (var connection = new SqliteConnection(
                             $"Data Source={database};Mode=ReadWrite;Pooling=False"))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                for (var index = 0; index < 502; index++)
                {
                    var contactId = index.ToString(
                        "x32", System.Globalization.CultureInfo.InvariantCulture);
                    await using var command = connection.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText =
                        """
                        INSERT INTO contacts(contact_id, created_utc) VALUES ($id, $utc);
                        INSERT INTO contact_revisions(
                            contact_id, revision, display_name, email_address, notes,
                            is_group, is_tombstoned, saved_utc)
                        VALUES ($id, 1, $name, $email, '', $isGroup, $isTombstoned, $utc);
                        """;
                    command.Parameters.AddWithValue("$id", contactId);
                    command.Parameters.AddWithValue("$utc", "2026-08-14T00:00:00.0000000+00:00");
                    command.Parameters.AddWithValue("$name", $"Contact {index:D3}");
                    command.Parameters.AddWithValue("$email", $"contact{index}@example.test");
                    command.Parameters.AddWithValue("$isGroup", index == 500 ? 1 : 0);
                    command.Parameters.AddWithValue("$isTombstoned", index == 501 ? 1 : 0);
                    await command.ExecuteNonQueryAsync();
                }
                await using (var member = connection.CreateCommand())
                {
                    member.Transaction = (SqliteTransaction)transaction;
                    member.CommandText =
                        """
                        INSERT INTO contact_revision_group_members(
                            contact_id, revision, ordinal, member_kind, member_value)
                        VALUES ($id, 1, 0, 'EmailAddress', 'group-member@example.test');
                        """;
                    member.Parameters.AddWithValue("$id", 500.ToString(
                        "x32", System.Globalization.CultureInfo.InvariantCulture));
                    await member.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
            }

            var contacts = await store.ReadActiveContactsForRecipientResolutionAsync();
            Assert.Equal(501, contacts.Count);
            Assert.DoesNotContain(contacts, contact => contact.DisplayName == "Contact 501");
            var group = contacts.Single(contact => contact.DisplayName == "Contact 500");
            Assert.True(group.IsGroup);
            Assert.Equal("group-member@example.test", Assert.Single(group.GroupMembers).Value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "NovaClient")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException();
        var root = Path.Combine(
            directory.FullName, ".artifacts", "contact-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
