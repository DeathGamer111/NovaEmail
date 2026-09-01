using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovaEmail.Storage;

namespace NovaEmail.Sync.Tests;

public sealed class AppleCalDavSyncTranscriptSourceTests
{
    private static readonly DateTimeOffset ObservedUtc =
        DateTimeOffset.UtcNow.AddMinutes(-2);

    [Fact]
    public async Task InitialSyncImportsStandaloneAndRecurringResourcesWithoutNetwork()
    {
        const string rawHref = "/principals/private/calendar/standalone.ics";
        const string rawEtag = "private-etag-standalone";
        const string rawSyncToken = "https://calendar.invalid/sync/private-initial-token";
        const string rawUid = "private-standalone-uid@example.test";
        var source = Source(null, $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:response>
                <D:href>{{rawHref}}</D:href>
                <D:propstat>
                  <D:prop>
                    <D:getetag>{{rawEtag}}</D:getetag>
                    <C:calendar-data>BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:{{rawUid}}
            DTSTART;VALUE=DATE:20271105
            DTEND;VALUE=DATE:20271106
            SUMMARY:All-day planning
            END:VEVENT
            END:VCALENDAR
            </C:calendar-data>
                  </D:prop>
                  <D:status>HTTP/1.1 200 OK</D:status>
                </D:propstat>
              </D:response>
              <D:response>
                <D:href>/principals/private/calendar/recurring.ics</D:href>
                <D:propstat>
                  <D:prop>
                    <D:getetag>private-etag-recurring</D:getetag>
                    <C:calendar-data>BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:private-recurring-uid@example.test
            DTSTART:20271108T150000Z
            DTEND:20271108T160000Z
            RRULE:FREQ=WEEKLY;COUNT=2
            SUMMARY:Weekly review
            END:VEVENT
            END:VCALENDAR
            </C:calendar-data>
                  </D:prop>
                  <D:status>HTTP/1.1 200 OK</D:status>
                </D:propstat>
              </D:response>
              <D:sync-token>{{rawSyncToken}}</D:sync-token>
            </D:multistatus>
            """);

        var batch = await source.ReadSnapshotBatchAsync();

        Assert.Equal(CalendarEventSourceKind.Apple, batch.SourceKind);
        Assert.True(batch.IsLocalProtocolDouble);
        Assert.False(batch.IsExplicitLocalFileImport);
        Assert.False(batch.NetworkActionsInvoked);
        Assert.False(batch.ProductionEndpointsInvoked);
        Assert.Equal(3, batch.Snapshots.Count);
        Assert.Equal(2, batch.SeriesSnapshots!.Count);
        Assert.All(batch.Snapshots, snapshot => Assert.NotNull(
            snapshot.ProviderSeriesIdentitySha256));
        Assert.Single(batch.Snapshots, snapshot => snapshot.AllDay);
        Assert.Equal(2, batch.Snapshots.Count(snapshot => !snapshot.AllDay));

        var serialized = JsonSerializer.Serialize(batch);
        Assert.DoesNotContain(rawHref, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(rawEtag, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(rawSyncToken, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(rawUid, serialized, StringComparison.Ordinal);

        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var coordinator = new LocalCalendarProviderImportCoordinator(store);
            var first = await coordinator.ImportAsync(source);
            var repeated = await coordinator.ImportAsync(source);

            Assert.Equal(3, first.Added);
            Assert.Equal(2, first.SeriesSnapshots);
            Assert.Equal(3, repeated.AlreadyObserved);
            Assert.False(first.NetworkActionsInvoked);
            Assert.False(first.ExternalMutationsAttempted);
            Assert.Equal(3, (await store.ReadLocalCalendarAgendaAsync(
                new DateTime(2027, 11, 1), new DateTime(2027, 11, 30))).Count);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task IncrementalNotFoundRetainsResourceHistoryAsTombstone()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var coordinator = new LocalCalendarProviderImportCoordinator(store);
            var initial = Source(null, ActiveResponse(
                "https://calendar.invalid/sync/initial",
                "event-etag-1"), ObservedUtc);
            var incremental = Source(
                "https://calendar.invalid/sync/initial",
                """
                <D:multistatus xmlns:D="DAV:">
                  <D:response>
                    <D:href>/calendar/event.ics</D:href>
                    <D:status>HTTP/1.1 404 Not Found</D:status>
                  </D:response>
                  <D:sync-token>https://calendar.invalid/sync/incremental</D:sync-token>
                </D:multistatus>
                """,
                ObservedUtc.AddSeconds(1));

            var added = await coordinator.ImportAsync(initial);
            var removed = await coordinator.ImportAsync(incremental);

            Assert.Equal(1, added.Added);
            Assert.Equal(1, removed.Tombstoned);
            Assert.Equal(1, removed.RemovedOccurrencesRetained);
            Assert.Equal(1, removed.SeriesSnapshots);
            Assert.Empty(await store.ReadLocalCalendarAgendaAsync(
                new DateTime(2027, 12, 1), new DateTime(2027, 12, 3)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DisplayProjectionChangesRevisionButRetainsResourceEventIdentity()
    {
        var response = ActiveResponse(
            "https://calendar.invalid/sync/zone",
            "zone-etag");
        var utc = Source(null, response, ObservedUtc, TimeZoneInfo.Utc);
        var minusEight = Source(
            null,
            response,
            ObservedUtc,
            TimeZoneInfo.CreateCustomTimeZone(
                "NovaEmail CalDAV UTC-8",
                TimeSpan.FromHours(-8),
                "NovaEmail CalDAV UTC-8",
                "NovaEmail CalDAV UTC-8"));

        var utcEvent = Assert.Single((await utc.ReadSnapshotBatchAsync()).Snapshots);
        var projected = Assert.Single((await minusEight.ReadSnapshotBatchAsync()).Snapshots);

        Assert.Equal(utcEvent.ProviderEventIdentitySha256, projected.ProviderEventIdentitySha256);
        Assert.NotEqual(
            utcEvent.ProviderRevisionIdentitySha256,
            projected.ProviderRevisionIdentitySha256);
        Assert.Equal(new DateTime(2027, 12, 1, 15, 0, 0), utcEvent.StartLocal);
        Assert.Equal(new DateTime(2027, 12, 1, 7, 0, 0), projected.StartLocal);
    }

    [Fact]
    public async Task InvalidTranscriptFailsBeforeAnyStorageMutation()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var coordinator = new LocalCalendarProviderImportCoordinator(store);
            var duplicateHref = Source(null, """
                <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
                  <D:response>
                    <D:href>/calendar/event.ics</D:href>
                    <D:propstat><D:prop><D:getetag>a</D:getetag><C:calendar-data>BEGIN:VCALENDAR
                VERSION:2.0
                BEGIN:VEVENT
                UID:first@example.test
                DTSTART:20271201T150000Z
                DTEND:20271201T160000Z
                SUMMARY:First
                END:VEVENT
                END:VCALENDAR
                </C:calendar-data></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
                  </D:response>
                  <D:response>
                    <D:href>/calendar/event.ics</D:href>
                    <D:propstat><D:prop><D:getetag>b</D:getetag><C:calendar-data>BEGIN:VCALENDAR
                VERSION:2.0
                BEGIN:VEVENT
                UID:second@example.test
                DTSTART:20271202T150000Z
                DTEND:20271202T160000Z
                SUMMARY:Second
                END:VEVENT
                END:VCALENDAR
                </C:calendar-data></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
                  </D:response>
                  <D:sync-token>https://calendar.invalid/sync/duplicate</D:sync-token>
                </D:multistatus>
                """);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                coordinator.ImportAsync(duplicateHref));
            Assert.Empty(await store.ReadLocalCalendarAgendaAsync(
                new DateTime(2027, 1, 1), new DateTime(2028, 1, 1)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("""
        <!DOCTYPE multistatus [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
        <D:multistatus xmlns:D="DAV:"><D:sync-token>urn:test:token</D:sync-token></D:multistatus>
        """)]
    [InlineData("""
        <D:multistatus xmlns:D="DAV:">
          <D:response><D:href>/calendar/event.ics</D:href><D:status>HTTP/1.1 507 Insufficient Storage</D:status></D:response>
          <D:sync-token>urn:test:token</D:sync-token>
        </D:multistatus>
        """)]
    [InlineData("""
        <D:multistatus xmlns:D="DAV:">
          <D:sync-token>not a URI token</D:sync-token>
        </D:multistatus>
        """)]
    public async Task DtdTruncationStatusAndInvalidTokenFailClosed(string response)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Source(null, response).ReadSnapshotBatchAsync());
    }

    [Fact]
    public async Task InitialDeletionAndMultiUidResourceFailClosed()
    {
        var initialDeletion = Source(null, """
            <D:multistatus xmlns:D="DAV:">
              <D:response><D:href>/calendar/event.ics</D:href><D:status>HTTP/1.1 404 Not Found</D:status></D:response>
              <D:sync-token>urn:test:token</D:sync-token>
            </D:multistatus>
            """);
        var multipleUid = Source(null, """
            <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:response>
                <D:href>/calendar/mixed.ics</D:href>
                <D:propstat><D:prop><D:getetag>mixed</D:getetag><C:calendar-data>BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:first@example.test
            DTSTART:20271201T150000Z
            DTEND:20271201T160000Z
            SUMMARY:First
            END:VEVENT
            BEGIN:VEVENT
            UID:second@example.test
            DTSTART:20271202T150000Z
            DTEND:20271202T160000Z
            SUMMARY:Second
            END:VEVENT
            END:VCALENDAR
            </C:calendar-data></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
              </D:response>
              <D:sync-token>urn:test:mixed</D:sync-token>
            </D:multistatus>
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            initialDeletion.ReadSnapshotBatchAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            multipleUid.ReadSnapshotBatchAsync());
    }

    [Fact]
    public async Task RequestShapeMustAttestSyncLevelAndRequiredProperties()
    {
        var response = Encoding.UTF8.GetBytes("""
            <D:multistatus xmlns:D="DAV:">
              <D:sync-token>urn:test:request-shape</D:sync-token>
            </D:multistatus>
            """);
        var account = Sha256("apple-caldav-transcript-account");
        AppleCalDavSyncTranscriptSource Unsafe(
            int syncLevel,
            bool getEtag,
            bool calendarData) => new(
                account,
                new AppleCalDavSyncTranscript(
                    null, syncLevel, getEtag, calendarData, response),
                TimeZoneInfo.Utc,
                ObservedUtc);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Unsafe(0, true, true).ReadSnapshotBatchAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Unsafe(1, false, true).ReadSnapshotBatchAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Unsafe(1, true, false).ReadSnapshotBatchAsync());
    }

    private static AppleCalDavSyncTranscriptSource Source(
        string? requestSyncToken,
        string response,
        DateTimeOffset? observedUtc = null,
        TimeZoneInfo? displayTimeZone = null) => new(
            Sha256("apple-caldav-transcript-account"),
            new AppleCalDavSyncTranscript(
                requestSyncToken,
                SyncLevel: 1,
                GetEtagRequested: true,
                CalendarDataRequested: true,
                Encoding.UTF8.GetBytes(response)),
            displayTimeZone ?? TimeZoneInfo.Utc,
            observedUtc ?? ObservedUtc);

    private static string ActiveResponse(string syncToken, string etag) => $$"""
        <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
          <D:response>
            <D:href>/calendar/event.ics</D:href>
            <D:propstat>
              <D:prop>
                <D:getetag>{{etag}}</D:getetag>
                <C:calendar-data>BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:private-event@example.test
        DTSTART:20271201T150000Z
        DTEND:20271201T160000Z
        SUMMARY:Local transcript event
        END:VEVENT
        END:VCALENDAR
        </C:calendar-data>
              </D:prop>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
          <D:sync-token>{{syncToken}}</D:sync-token>
        </D:multistatus>
        """;

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
        var root = Path.Combine(
            "C:\\projects\\NovaEmail8",
            ".artifacts",
            "sync-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
