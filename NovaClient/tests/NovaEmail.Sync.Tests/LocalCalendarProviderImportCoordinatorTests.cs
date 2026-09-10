using System.Security.Cryptography;
using System.Text;
using NovaEmail.Storage;

namespace NovaEmail.Sync.Tests;

public sealed class LocalCalendarProviderImportCoordinatorTests
{
    private static readonly DateTime StartLocal =
        new(2027, 10, 2, 14, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTimeOffset ObservedUtc =
        DateTimeOffset.UtcNow.AddMinutes(-1);

    [Fact]
    public async Task LocalGoogleDoubleImportsIdempotentlyWithoutNetworkOrExternalMutation()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var coordinator = new LocalCalendarProviderImportCoordinator(store);
            var accountIdentity = Sha256("google-account");
            var snapshot = Snapshot(
                CalendarEventSourceKind.Google, accountIdentity,
                Sha256("google-event"), Sha256("google-revision"));
            var source = new FakeLocalCalendarSource(new LocalCalendarProviderSnapshotBatch(
                CalendarEventSourceKind.Google,
                accountIdentity,
                [snapshot],
                IsLocalProtocolDouble: true,
                NetworkActionsInvoked: false,
                ProductionEndpointsInvoked: false));

            var first = await coordinator.ImportAsync(source);
            var repeated = await coordinator.ImportAsync(source);

            Assert.Equal(1, first.Observed);
            Assert.Equal(1, first.Applied);
            Assert.Equal(0, first.AlreadyObserved);
            Assert.Equal(1, first.Added);
            Assert.Equal(0, repeated.Applied);
            Assert.Equal(1, repeated.AlreadyObserved);
            Assert.Equal(0, repeated.Added);
            Assert.False(first.ExternalMutationsAttempted);
            Assert.False(first.ExplicitLocalFileRead);
            Assert.False(first.NetworkActionsInvoked);
            Assert.False(first.ProductionEndpointsInvoked);
            Assert.Single(await store.ReadLocalCalendarAgendaAsync(
                StartLocal.Date, StartLocal.Date.AddDays(1)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LocalAppleDoubleRetainsMissingDeletionWithoutInventingAnEvent()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var coordinator = new LocalCalendarProviderImportCoordinator(store);
            var accountIdentity = Sha256("apple-account");
            var snapshot = Snapshot(
                CalendarEventSourceKind.Apple, accountIdentity,
                Sha256("missing-event"), Sha256("deletion-revision")) with
            {
                IsDeleted = true,
                Title = string.Empty,
                StartLocal = default,
                EndLocal = default,
                Location = string.Empty,
                Notes = string.Empty,
            };
            var result = await coordinator.ImportAsync(new FakeLocalCalendarSource(
                new LocalCalendarProviderSnapshotBatch(
                    CalendarEventSourceKind.Apple,
                    accountIdentity,
                    [snapshot],
                    IsLocalProtocolDouble: true,
                    NetworkActionsInvoked: false,
                    ProductionEndpointsInvoked: false)));

            Assert.Equal(1, result.Applied);
            Assert.Equal(1, result.MissingDeletionRetained);
            Assert.Empty(await store.ReadLocalCalendarAgendaAsync(
                StartLocal.Date, StartLocal.Date.AddDays(1)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderBatchPreflightRejectsUnsafeOrMixedSourcesBeforeStorageMutation()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var coordinator = new LocalCalendarProviderImportCoordinator(store);
            var accountIdentity = Sha256("google-account");
            var valid = Snapshot(
                CalendarEventSourceKind.Google, accountIdentity,
                Sha256("event-1"), Sha256("revision-1"));
            var baseBatch = new LocalCalendarProviderSnapshotBatch(
                CalendarEventSourceKind.Google,
                accountIdentity,
                [valid],
                IsLocalProtocolDouble: true,
                NetworkActionsInvoked: false,
                ProductionEndpointsInvoked: false);

            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ImportAsync(
                new FakeLocalCalendarSource(baseBatch with { IsLocalProtocolDouble = false })));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ImportAsync(
                new FakeLocalCalendarSource(baseBatch with
                {
                    IsExplicitLocalFileImport = true,
                })));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ImportAsync(
                new FakeLocalCalendarSource(baseBatch with { NetworkActionsInvoked = true })));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ImportAsync(
                new FakeLocalCalendarSource(baseBatch with { ProductionEndpointsInvoked = true })));
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.ImportAsync(
                new FakeLocalCalendarSource(baseBatch with
                {
                    Snapshots =
                    [
                        valid,
                        Snapshot(
                            CalendarEventSourceKind.Apple, accountIdentity,
                            Sha256("event-2"), Sha256("revision-2")),
                    ],
                })));
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.ImportAsync(
                new FakeLocalCalendarSource(baseBatch with
                {
                    Snapshots =
                    [
                        valid,
                        valid with { ProviderRevisionIdentitySha256 = Sha256("revision-2") },
                    ],
                })));
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.ImportAsync(
                new FakeLocalCalendarSource(baseBatch with
                {
                    Snapshots = Enumerable.Repeat(
                        valid, LocalCalendarProviderImportCoordinator.MaximumSnapshotsPerBatch + 1).ToArray(),
                })));

            Assert.Empty(await store.ReadLocalCalendarAgendaAsync(
                StartLocal.Date, StartLocal.Date.AddDays(1)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteRecurringSeriesSnapshotRetainsRemovedOccurrenceAsTombstone()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var coordinator = new LocalCalendarProviderImportCoordinator(store);
            var accountIdentity = Sha256("recurring-google-account");
            var seriesIdentity = Sha256("recurring-google-series");
            var firstEventIdentity = Sha256("recurring-google-occurrence-1");
            var secondEventIdentity = Sha256("recurring-google-occurrence-2");
            var firstSeriesRevision = Sha256("recurring-google-series-revision-1");
            var secondSeriesRevision = Sha256("recurring-google-series-revision-2");
            var firstOccurrence = Snapshot(
                CalendarEventSourceKind.Google, accountIdentity,
                firstEventIdentity, Sha256("occurrence-revision-1")) with
            {
                ProviderSeriesIdentitySha256 = seriesIdentity,
            };
            var secondOccurrence = Snapshot(
                CalendarEventSourceKind.Google, accountIdentity,
                secondEventIdentity, Sha256("occurrence-revision-2")) with
            {
                StartLocal = StartLocal.AddDays(7),
                EndLocal = StartLocal.AddDays(7).AddHours(1),
                ProviderSeriesIdentitySha256 = seriesIdentity,
            };
            var initialSeries = new CalendarProviderSeriesSnapshotImport(
                CalendarEventSourceKind.Google,
                accountIdentity,
                seriesIdentity,
                firstSeriesRevision,
                [firstEventIdentity, secondEventIdentity],
                ObservedUtc);
            var initial = new LocalCalendarProviderSnapshotBatch(
                CalendarEventSourceKind.Google,
                accountIdentity,
                [firstOccurrence, secondOccurrence],
                IsLocalProtocolDouble: true,
                NetworkActionsInvoked: false,
                ProductionEndpointsInvoked: false,
                SeriesSnapshots: [initialSeries]);

            var first = await coordinator.ImportAsync(new FakeLocalCalendarSource(initial));
            Assert.Equal(2, first.Added);
            Assert.Equal(1, first.SeriesSnapshots);
            Assert.Equal(0, first.RemovedOccurrencesRetained);

            var retainedOccurrence = secondOccurrence with
            {
                ProviderRevisionIdentitySha256 = Sha256("occurrence-revision-3"),
                Title = "Provider test event revised",
                ObservedUtc = ObservedUtc.AddSeconds(1),
            };
            var revisedSeries = initialSeries with
            {
                ProviderSeriesRevisionIdentitySha256 = secondSeriesRevision,
                MemberEventIdentitySha256s = [secondEventIdentity],
                ObservedUtc = ObservedUtc.AddSeconds(1),
            };
            var revised = initial with
            {
                Snapshots = [retainedOccurrence],
                SeriesSnapshots = [revisedSeries],
            };

            var second = await coordinator.ImportAsync(new FakeLocalCalendarSource(revised));
            var repeated = await coordinator.ImportAsync(new FakeLocalCalendarSource(revised));

            Assert.Equal(2, second.Observed);
            Assert.Equal(1, second.Updated);
            Assert.Equal(1, second.Tombstoned);
            Assert.Equal(1, second.RemovedOccurrencesRetained);
            Assert.Equal(1, repeated.Observed);
            Assert.Equal(1, repeated.AlreadyObserved);
            Assert.Equal(0, repeated.RemovedOccurrencesRetained);
            var agenda = await store.ReadLocalCalendarAgendaAsync(
                StartLocal.Date, StartLocal.Date.AddDays(8), maximumCount: 10);
            var remaining = Assert.Single(agenda);
            Assert.Equal(secondEventIdentity, remaining.ProviderEventId);
            var firstEvent = (await store.ReadLocalCalendarEventRevisionHistoryAsync(
                (await store.ReadCalendarProviderObservationHistoryAsync(
                    CalendarEventSourceKind.Google,
                    accountIdentity,
                    firstEventIdentity))[0].EventId!));
            Assert.True(firstEvent[^1].IsTombstoned);
            var latestSeries = await store.ReadLatestCalendarProviderSeriesSnapshotAsync(
                CalendarEventSourceKind.Google, accountIdentity, seriesIdentity);
            Assert.Equal([secondEventIdentity], latestSeries!.MemberEventIdentitySha256s);

            var staleSeries = revisedSeries with
            {
                ProviderSeriesRevisionIdentitySha256 = Sha256("stale-series-revision"),
                ObservedUtc = ObservedUtc,
            };
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.ImportAsync(
                new FakeLocalCalendarSource(revised with
                {
                    Snapshots =
                    [
                        retainedOccurrence with
                        {
                            ProviderRevisionIdentitySha256 = Sha256("stale-occurrence-revision"),
                            ObservedUtc = ObservedUtc,
                        },
                    ],
                    SeriesSnapshots = [staleSeries],
                })));
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
        string revisionIdentity) =>
        new(
            sourceKind,
            accountIdentity,
            eventIdentity,
            revisionIdentity,
            Title: "Provider test event",
            StartLocal,
            StartLocal.AddHours(1),
            AllDay: false,
            Location: string.Empty,
            Notes: "Local-double snapshot only.",
            IsDeleted: false,
            ObservedUtc);

    private static string Sha256(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<ModernMailStore> CreateStoreAsync(string testRoot)
    {
        var dataRoot = Path.Combine(testRoot, "data");
        var store = new ModernMailStore(Path.Combine(dataRoot, "mail.db"), dataRoot);
        await store.InitializeAsync();
        return store;
    }

    private static string CreateTestRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "NovaClient")) &&
                Directory.Exists(Path.Combine(directory.FullName, "TestLab")))
            {
                var root = Path.Combine(
                    directory.FullName, ".artifacts", "sync-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                return root;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private sealed class FakeLocalCalendarSource(LocalCalendarProviderSnapshotBatch batch)
        : ILocalCalendarProviderSnapshotSource
    {
        public Task<LocalCalendarProviderSnapshotBatch> ReadSnapshotBatchAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(batch);
    }
}
