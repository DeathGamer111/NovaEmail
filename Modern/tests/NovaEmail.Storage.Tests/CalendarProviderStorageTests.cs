using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NovaEmail.Storage;

namespace NovaEmail.Storage.Tests;

public sealed class CalendarProviderStorageTests
{
    private static readonly DateTime StartLocal =
        new(2027, 9, 15, 9, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTimeOffset ObservedUtc =
        DateTimeOffset.UtcNow.AddMinutes(-1);

    [Fact]
    public async Task GoogleSnapshotsAreIdempotentAppendOnlyAndDeletionRetaining()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "mail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            const string rawAccountIdentity = "austin@example.test";
            const string rawEventIdentity = "google-private-event-id";
            var accountIdentity = Sha256(rawAccountIdentity);
            var eventIdentity = Sha256(rawEventIdentity);

            var added = await store.ApplyCalendarProviderEventSnapshotAsync(Snapshot(
                CalendarEventSourceKind.Google, accountIdentity, eventIdentity, "revision-1",
                "Planning session", StartLocal, StartLocal.AddHours(1)));
            var repeated = await store.ApplyCalendarProviderEventSnapshotAsync(Snapshot(
                CalendarEventSourceKind.Google, accountIdentity, eventIdentity, "revision-1",
                "Planning session", StartLocal, StartLocal.AddHours(1)));
            var updated = await store.ApplyCalendarProviderEventSnapshotAsync(Snapshot(
                CalendarEventSourceKind.Google, accountIdentity, eventIdentity, "revision-2",
                "Planning session revised", StartLocal, StartLocal.AddMinutes(90)));
            var unchanged = await store.ApplyCalendarProviderEventSnapshotAsync(Snapshot(
                CalendarEventSourceKind.Google, accountIdentity, eventIdentity, "revision-3",
                "Planning session revised", StartLocal, StartLocal.AddMinutes(90)));
            var tombstoned = await store.ApplyCalendarProviderEventSnapshotAsync(DeletedSnapshot(
                CalendarEventSourceKind.Google, accountIdentity, eventIdentity, "revision-4"));
            var alreadyTombstoned = await store.ApplyCalendarProviderEventSnapshotAsync(DeletedSnapshot(
                CalendarEventSourceKind.Google, accountIdentity, eventIdentity, "revision-5"));
            var resurrection = await store.ApplyCalendarProviderEventSnapshotAsync(Snapshot(
                CalendarEventSourceKind.Google, accountIdentity, eventIdentity, "revision-6",
                "Provider resurrection attempt", StartLocal, StartLocal.AddHours(2)));

            Assert.Equal(CalendarProviderObservationOutcome.Added, added.Observation.Outcome);
            Assert.False(added.WasAlreadyObserved);
            Assert.Equal(1, added.Event!.Revision);
            Assert.Equal(accountIdentity, added.Event.ProviderAccountId);
            Assert.Equal(eventIdentity, added.Event.ProviderEventId);
            Assert.True(repeated.WasAlreadyObserved);
            Assert.Equal(added.Observation, repeated.Observation);
            Assert.Equal(CalendarProviderObservationOutcome.Updated, updated.Observation.Outcome);
            Assert.Equal(2, updated.Event!.Revision);
            Assert.Equal(CalendarProviderObservationOutcome.Unchanged, unchanged.Observation.Outcome);
            Assert.Equal(2, unchanged.Event!.Revision);
            Assert.Equal(CalendarProviderObservationOutcome.Tombstoned, tombstoned.Observation.Outcome);
            Assert.True(tombstoned.Event!.IsTombstoned);
            Assert.Equal(3, tombstoned.Event.Revision);
            Assert.Equal(
                CalendarProviderObservationOutcome.AlreadyTombstoned,
                alreadyTombstoned.Observation.Outcome);
            Assert.Equal(
                CalendarProviderObservationOutcome.ResurrectionConflictRetained,
                resurrection.Observation.Outcome);
            Assert.True(resurrection.Event!.IsTombstoned);
            Assert.Equal(3, resurrection.Event.Revision);

            var revisions = await store.ReadLocalCalendarEventRevisionHistoryAsync(
                added.Event.EventId);
            Assert.Equal([1, 2, 3], revisions.Select(item => item.Revision));
            Assert.True(revisions[2].IsTombstoned);
            var observations = await store.ReadCalendarProviderObservationHistoryAsync(
                CalendarEventSourceKind.Google, accountIdentity, eventIdentity);
            Assert.Equal(6, observations.Count);
            Assert.Equal(
                [
                    CalendarProviderObservationOutcome.Added,
                    CalendarProviderObservationOutcome.Updated,
                    CalendarProviderObservationOutcome.Unchanged,
                    CalendarProviderObservationOutcome.Tombstoned,
                    CalendarProviderObservationOutcome.AlreadyTombstoned,
                    CalendarProviderObservationOutcome.ResurrectionConflictRetained,
                ],
                observations.Select(item => item.Outcome));
            Assert.Empty(await store.ReadLocalCalendarAgendaAsync(
                StartLocal.Date, StartLocal.Date.AddDays(1)));
            Assert.Equal(10, (await store.ReadNoPurgeCompactionAssessmentAsync()).ImmutableRevisionCount);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ApplyCalendarProviderEventSnapshotAsync(Snapshot(
                    CalendarEventSourceKind.Google, accountIdentity, eventIdentity, "revision-6",
                    "Different bytes under a reused provider revision", StartLocal,
                    StartLocal.AddHours(2))));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ApplyCalendarProviderEventSnapshotAsync(Snapshot(
                    CalendarEventSourceKind.Google, accountIdentity.ToUpperInvariant(), eventIdentity,
                    "revision-7", "Uppercase identity", StartLocal, StartLocal.AddHours(1))));

            await using var connection = new SqliteConnection(
                $"Data Source={database};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText =
                "UPDATE calendar_provider_observations SET observed_utc=observed_utc;";
            var updateError = await Assert.ThrowsAsync<SqliteException>(
                () => update.ExecuteNonQueryAsync());
            Assert.Contains("immutable", updateError.Message, StringComparison.OrdinalIgnoreCase);
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM calendar_provider_observations;";
            var deleteError = await Assert.ThrowsAsync<SqliteException>(
                () => delete.ExecuteNonQueryAsync());
            Assert.Contains("purge is disabled", deleteError.Message, StringComparison.OrdinalIgnoreCase);

            foreach (var path in Directory.GetFiles(dataRoot, "*", SearchOption.AllDirectories))
            {
                var persistedText = Encoding.UTF8.GetString(await ReadAllBytesSharedAsync(path));
                Assert.DoesNotContain(rawAccountIdentity, persistedText, StringComparison.Ordinal);
                Assert.DoesNotContain(rawEventIdentity, persistedText, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AppleMissingDeletionIsRetainedAsObservationWithoutCreatingAnEvent()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = new ModernMailStore(
                Path.Combine(testRoot, "data", "mail.db"), Path.Combine(testRoot, "data"));
            await store.InitializeAsync();
            var accountIdentity = Sha256("apple-account");
            var eventIdentity = Sha256("missing-apple-event");

            var result = await store.ApplyCalendarProviderEventSnapshotAsync(DeletedSnapshot(
                CalendarEventSourceKind.Apple, accountIdentity, eventIdentity, "apple-revision-1"));

            Assert.Equal(
                CalendarProviderObservationOutcome.MissingDeletionRetained,
                result.Observation.Outcome);
            Assert.Null(result.Event);
            Assert.Null(result.Observation.EventId);
            Assert.Single(await store.ReadCalendarProviderObservationHistoryAsync(
                CalendarEventSourceKind.Apple, accountIdentity, eventIdentity));
            var resurrection = await store.ApplyCalendarProviderEventSnapshotAsync(Snapshot(
                CalendarEventSourceKind.Apple, accountIdentity, eventIdentity,
                "apple-revision-2", "Late provider add", StartLocal, StartLocal.AddHours(1)));
            Assert.Equal(
                CalendarProviderObservationOutcome.ResurrectionConflictRetained,
                resurrection.Observation.Outcome);
            Assert.Null(resurrection.Event);
            Assert.Null(resurrection.Observation.EventId);
            Assert.Equal(2, (await store.ReadCalendarProviderObservationHistoryAsync(
                CalendarEventSourceKind.Apple, accountIdentity, eventIdentity)).Count);
            Assert.Empty(await store.ReadLocalCalendarAgendaAsync(
                StartLocal.Date, StartLocal.Date.AddDays(1)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RecurringSeriesSnapshotsAreAppendOnlyAndMembershipIsHashBound()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var database = Path.Combine(dataRoot, "mail.db");
            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();
            var accountIdentity = Sha256("series-account");
            var seriesIdentity = Sha256("provider-series");
            var firstEventIdentity = Sha256("series-occurrence-1");
            var secondEventIdentity = Sha256("series-occurrence-2");
            var firstRevisionIdentity = Sha256("series-revision-1");
            var secondRevisionIdentity = Sha256("series-revision-2");

            var firstEvent = Snapshot(
                CalendarEventSourceKind.Google, accountIdentity, firstEventIdentity,
                "occurrence-revision-1", "First occurrence", StartLocal,
                StartLocal.AddHours(1)) with
            {
                ProviderSeriesIdentitySha256 = seriesIdentity,
            };
            var secondEvent = Snapshot(
                CalendarEventSourceKind.Google, accountIdentity, secondEventIdentity,
                "occurrence-revision-2", "Second occurrence", StartLocal.AddDays(7),
                StartLocal.AddDays(7).AddHours(1)) with
            {
                ProviderSeriesIdentitySha256 = seriesIdentity,
            };
            await store.ApplyCalendarProviderEventSnapshotAsync(firstEvent);
            await store.ApplyCalendarProviderEventSnapshotAsync(secondEvent);
            var firstSeries = new CalendarProviderSeriesSnapshotImport(
                CalendarEventSourceKind.Google,
                accountIdentity,
                seriesIdentity,
                firstRevisionIdentity,
                [secondEventIdentity, firstEventIdentity],
                ObservedUtc);

            var saved = await store.SaveCalendarProviderSeriesSnapshotAsync(firstSeries);
            var repeated = await store.SaveCalendarProviderSeriesSnapshotAsync(firstSeries);
            var latest = await store.ReadLatestCalendarProviderSeriesSnapshotAsync(
                CalendarEventSourceKind.Google, accountIdentity, seriesIdentity);

            Assert.False(saved.WasAlreadyPresent);
            Assert.True(repeated.WasAlreadyPresent);
            Assert.NotNull(latest);
            Assert.Equal(
                new[] { firstEventIdentity, secondEventIdentity }.Order(StringComparer.Ordinal),
                latest.MemberEventIdentitySha256s);
            Assert.Equal(firstRevisionIdentity, latest.ProviderSeriesRevisionIdentitySha256);
            Assert.Equal(seriesIdentity, saved.Snapshot.ProviderSeriesIdentitySha256);
            Assert.Equal(seriesIdentity, (await store.ReadLocalCalendarAgendaAsync(
                StartLocal.Date, StartLocal.Date.AddDays(8), maximumCount: 10))[0]
                .ProviderSeriesId);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveCalendarProviderSeriesSnapshotAsync(firstSeries with
                {
                    MemberEventIdentitySha256s = [firstEventIdentity],
                }));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ApplyCalendarProviderEventSnapshotAsync(firstEvent with
                {
                    ProviderRevisionIdentitySha256 = Sha256("wrong-series-revision"),
                    ProviderSeriesIdentitySha256 = Sha256("different-series"),
                }));

            var secondSeries = firstSeries with
            {
                ProviderSeriesRevisionIdentitySha256 = secondRevisionIdentity,
                MemberEventIdentitySha256s = [secondEventIdentity],
                ObservedUtc = ObservedUtc.AddSeconds(1),
            };
            var advanced = await store.SaveCalendarProviderSeriesSnapshotAsync(secondSeries);
            Assert.False(advanced.WasAlreadyPresent);
            latest = await store.ReadLatestCalendarProviderSeriesSnapshotAsync(
                CalendarEventSourceKind.Google, accountIdentity, seriesIdentity);
            Assert.Equal([secondEventIdentity], latest!.MemberEventIdentitySha256s);

            await using var connection = new SqliteConnection(
                $"Data Source={database};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText =
                "UPDATE calendar_provider_series_snapshots SET observed_utc=observed_utc;";
            var updateError = await Assert.ThrowsAsync<SqliteException>(
                () => update.ExecuteNonQueryAsync());
            Assert.Contains("immutable", updateError.Message, StringComparison.OrdinalIgnoreCase);
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM calendar_provider_series_snapshot_members;";
            var deleteError = await Assert.ThrowsAsync<SqliteException>(
                () => delete.ExecuteNonQueryAsync());
            Assert.Contains("purge is disabled", deleteError.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingProviderRowsGainNullableSeriesProvenanceWithoutRewrite()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            Directory.CreateDirectory(dataRoot);
            var database = Path.Combine(dataRoot, "mail.db");
            var accountIdentity = Sha256("pre-series-account");
            var eventIdentity = Sha256("pre-series-event");
            var revisionIdentity = Sha256("pre-series-revision");
            var payloadIdentity = Sha256("pre-series-payload");
            await using (var connection = new SqliteConnection(
                             $"Data Source={database};Mode=ReadWriteCreate;Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    PRAGMA foreign_keys=ON;
                    CREATE TABLE calendar_events(
                        event_id TEXT PRIMARY KEY,
                        created_utc TEXT NOT NULL) STRICT;
                    CREATE TABLE calendar_event_sources(
                        event_id TEXT PRIMARY KEY REFERENCES calendar_events(event_id),
                        source_kind TEXT NOT NULL,
                        evidence_id TEXT NULL,
                        source_message_identity_sha256 TEXT NULL,
                        provider_account_id TEXT NULL,
                        provider_event_id TEXT NULL,
                        created_utc TEXT NOT NULL) STRICT;
                    CREATE TABLE calendar_provider_observations(
                        source_kind TEXT NOT NULL,
                        provider_account_identity_sha256 TEXT NOT NULL,
                        provider_event_identity_sha256 TEXT NOT NULL,
                        provider_revision_identity_sha256 TEXT NOT NULL,
                        payload_sha256 TEXT NOT NULL,
                        event_id TEXT NULL REFERENCES calendar_events(event_id),
                        outcome TEXT NOT NULL,
                        observed_utc TEXT NOT NULL,
                        PRIMARY KEY(
                            source_kind, provider_account_identity_sha256,
                            provider_event_identity_sha256,
                            provider_revision_identity_sha256)) STRICT;
                    INSERT INTO calendar_events(event_id, created_utc)
                    VALUES ('0123456789abcdef0123456789abcdef', $observedUtc);
                    INSERT INTO calendar_event_sources(
                        event_id, source_kind, evidence_id,
                        source_message_identity_sha256, provider_account_id,
                        provider_event_id, created_utc)
                    VALUES ('0123456789abcdef0123456789abcdef', 'Google', NULL, NULL,
                        $accountIdentity, $eventIdentity, $observedUtc);
                    INSERT INTO calendar_provider_observations(
                        source_kind, provider_account_identity_sha256,
                        provider_event_identity_sha256,
                        provider_revision_identity_sha256, payload_sha256,
                        event_id, outcome, observed_utc)
                    VALUES ('Google', $accountIdentity, $eventIdentity,
                        $revisionIdentity, $payloadIdentity,
                        '0123456789abcdef0123456789abcdef', 'Added', $observedUtc);
                    """;
                command.Parameters.AddWithValue(
                    "$observedUtc", ObservedUtc.ToString("O"));
                command.Parameters.AddWithValue("$accountIdentity", accountIdentity);
                command.Parameters.AddWithValue("$eventIdentity", eventIdentity);
                command.Parameters.AddWithValue("$revisionIdentity", revisionIdentity);
                command.Parameters.AddWithValue("$payloadIdentity", payloadIdentity);
                await command.ExecuteNonQueryAsync();
            }

            var store = new ModernMailStore(database, dataRoot);
            await store.InitializeAsync();

            var observations = await store.ReadCalendarProviderObservationHistoryAsync(
                CalendarEventSourceKind.Google, accountIdentity, eventIdentity);
            var observation = Assert.Single(observations);
            Assert.Equal(payloadIdentity, observation.PayloadSha256);
            Assert.Null(observation.ProviderSeriesIdentitySha256);
            await using var verified = new SqliteConnection(
                $"Data Source={database};Mode=ReadWrite;Pooling=False");
            await verified.OpenAsync();
            await using var columns = verified.CreateCommand();
            columns.CommandText =
                """
                SELECT
                    (SELECT COUNT(*) FROM pragma_table_info('calendar_event_sources')
                        WHERE name='provider_series_id'),
                    (SELECT COUNT(*) FROM pragma_table_info('calendar_provider_observations')
                        WHERE name='provider_series_identity_sha256'),
                    (SELECT COUNT(*) FROM calendar_provider_observations),
                    (SELECT COUNT(*) FROM calendar_event_sources);
                """;
            await using var reader = await columns.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt64(1));
            Assert.Equal(1, reader.GetInt64(2));
            Assert.Equal(1, reader.GetInt64(3));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static CalendarProviderEventSnapshotImport Snapshot(
        CalendarEventSourceKind sourceKind,
        string accountIdentity,
        string eventIdentity,
        string rawRevisionIdentity,
        string title,
        DateTime startLocal,
        DateTime endLocal) =>
        new(
            sourceKind,
            accountIdentity,
            eventIdentity,
            Sha256(rawRevisionIdentity),
            title,
            startLocal,
            endLocal,
            AllDay: false,
            Location: "Conference room",
            Notes: "Retained provider snapshot.",
            IsDeleted: false,
            ObservedUtc);

    private static CalendarProviderEventSnapshotImport DeletedSnapshot(
        CalendarEventSourceKind sourceKind,
        string accountIdentity,
        string eventIdentity,
        string rawRevisionIdentity) =>
        new(
            sourceKind,
            accountIdentity,
            eventIdentity,
            Sha256(rawRevisionIdentity),
            Title: string.Empty,
            StartLocal: default,
            EndLocal: default,
            AllDay: false,
            Location: string.Empty,
            Notes: string.Empty,
            IsDeleted: true,
            ObservedUtc);

    private static string Sha256(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<byte[]> ReadAllBytesSharedAsync(string path)
    {
        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream();
        await input.CopyToAsync(output);
        return output.ToArray();
    }

    private static string CreateTestRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Modern")) &&
                Directory.Exists(Path.Combine(directory.FullName, "TestLab")))
            {
                var root = Path.Combine(
                    directory.FullName, ".artifacts", "storage-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                return root;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
