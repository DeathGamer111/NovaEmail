using System.Security.Cryptography;
using System.Text;
using NovaEmail.Storage;

namespace NovaEmail.Sync.Tests;

public sealed class GoogleCalendarEventsListTranscriptSourceTests
{
    private static readonly DateTimeOffset ObservedUtc =
        DateTimeOffset.UtcNow.AddMinutes(-1);

    [Fact]
    public async Task CompletePagedTranscriptImportsExpandedEventsWithoutNetwork()
    {
        var account = Sha256("google-transcript-account");
        var source = new GoogleCalendarEventsListTranscriptSource(
            account,
            [
                Page(null, """
                {
                  "kind": "calendar#events",
                  "nextPageToken": "page-2",
                  "items": [
                    {
                      "kind": "calendar#event",
                      "etag": "etag-1",
                      "id": "standalone-1",
                      "status": "confirmed",
                      "eventType": "default",
                      "summary": "All-day planning",
                      "start": { "date": "2027-11-05" },
                      "end": { "date": "2027-11-06" }
                    }
                  ]
                }
                """),
                Page("page-2", """
                {
                  "kind": "calendar#events",
                  "nextSyncToken": "opaque-sync-token",
                  "items": [
                    {
                      "kind": "calendar#event",
                      "etag": "etag-2",
                      "id": "series-instance-1",
                      "status": "tentative",
                      "summary": "Moved series meeting",
                      "description": "Read-only local transcript.",
                      "location": "Room 8",
                      "recurringEventId": "series-1",
                      "originalStartTime": { "dateTime": "2027-11-05T15:00:00Z" },
                      "start": { "dateTime": "2027-11-05T16:00:00Z" },
                      "end": { "dateTime": "2027-11-05T17:00:00Z" }
                    }
                  ]
                }
                """),
            ],
            TimeZoneInfo.Utc,
            ObservedUtc);

        var batch = await source.ReadSnapshotBatchAsync();

        Assert.Equal(CalendarEventSourceKind.Google, batch.SourceKind);
        Assert.Equal(account, batch.ProviderAccountIdentitySha256);
        Assert.True(batch.IsLocalProtocolDouble);
        Assert.False(batch.NetworkActionsInvoked);
        Assert.False(batch.ProductionEndpointsInvoked);
        Assert.Equal(2, batch.Snapshots.Count);
        var allDay = Assert.Single(batch.Snapshots, snapshot => snapshot.AllDay);
        Assert.Equal(new DateTime(2027, 11, 5), allDay.StartLocal);
        var timed = Assert.Single(batch.Snapshots, snapshot => !snapshot.AllDay);
        Assert.Equal(new DateTime(2027, 11, 5, 16, 0, 0), timed.StartLocal);
        var series = Assert.Single(batch.SeriesSnapshots!);
        Assert.Equal([timed.ProviderEventIdentitySha256], series.MemberEventIdentitySha256s);

        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var coordinator = new LocalCalendarProviderImportCoordinator(store);
            var first = await coordinator.ImportAsync(source);
            var repeated = await coordinator.ImportAsync(source);

            Assert.Equal(2, first.Added);
            Assert.Equal(1, first.SeriesSnapshots);
            Assert.Equal(2, repeated.AlreadyObserved);
            Assert.Equal(2, (await store.ReadLocalCalendarAgendaAsync(
                new DateTime(2027, 11, 5), new DateTime(2027, 11, 6))).Count);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledRecurringInstanceIsRetainedOutsideCurrentMembership()
    {
        var source = Source("""
        {
          "kind": "calendar#events",
          "nextSyncToken": "sync-cancelled",
          "items": [
            {
              "etag": "etag-active",
              "id": "active-instance",
              "status": "confirmed",
              "recurringEventId": "series-a",
              "originalStartTime": { "date": "2027-12-01" },
              "summary": "Active occurrence",
              "start": { "date": "2027-12-01" },
              "end": { "date": "2027-12-02" }
            },
            {
              "etag": "etag-cancelled",
              "id": "cancelled-instance",
              "status": "cancelled",
              "recurringEventId": "series-a",
              "originalStartTime": { "date": "2027-12-08" }
            }
          ]
        }
        """);

        var batch = await source.ReadSnapshotBatchAsync();

        var cancelled = Assert.Single(batch.Snapshots, snapshot => snapshot.IsDeleted);
        var series = Assert.Single(batch.SeriesSnapshots!);
        Assert.DoesNotContain(cancelled.ProviderEventIdentitySha256, series.MemberEventIdentitySha256s);
        Assert.Single(series.MemberEventIdentitySha256s);
    }

    [Fact]
    public async Task OriginalOccurrenceIdentitySurvivesDisplayTimeZoneProjection()
    {
        const string response = """
        {
          "kind": "calendar#events",
          "nextSyncToken": "sync-zone",
          "items": [
            {
              "etag": "etag-zone",
              "id": "instance-zone",
              "status": "confirmed",
              "recurringEventId": "series-zone",
              "originalStartTime": { "dateTime": "2027-11-05T15:00:00Z" },
              "summary": "Zone projection",
              "start": { "dateTime": "2027-11-05T16:00:00Z" },
              "end": { "dateTime": "2027-11-05T17:00:00Z" }
            }
          ]
        }
        """;
        var account = Sha256("google-zone-account");
        var utc = new GoogleCalendarEventsListTranscriptSource(
            account, [Page(null, response)], TimeZoneInfo.Utc, ObservedUtc);
        var minusEight = new GoogleCalendarEventsListTranscriptSource(
            account,
            [Page(null, response)],
            TimeZoneInfo.CreateCustomTimeZone(
                "NovaEmail transcript UTC-8",
                TimeSpan.FromHours(-8),
                "NovaEmail transcript UTC-8",
                "NovaEmail transcript UTC-8"),
            ObservedUtc);

        var utcEvent = Assert.Single((await utc.ReadSnapshotBatchAsync()).Snapshots);
        var projectedEvent = Assert.Single((await minusEight.ReadSnapshotBatchAsync()).Snapshots);

        Assert.Equal(utcEvent.ProviderEventIdentitySha256, projectedEvent.ProviderEventIdentitySha256);
        Assert.NotEqual(
            utcEvent.ProviderRevisionIdentitySha256,
            projectedEvent.ProviderRevisionIdentitySha256);
        Assert.Equal(new DateTime(2027, 11, 5, 16, 0, 0), utcEvent.StartLocal);
        Assert.Equal(new DateTime(2027, 11, 5, 8, 0, 0), projectedEvent.StartLocal);
    }

    [Fact]
    public async Task IncrementalOrBrokenPaginationTranscriptFailsClosed()
    {
        var account = Sha256("google-transcript-account");
        var incremental = new GoogleCalendarEventsListTranscriptSource(
            account,
            [new GoogleCalendarEventsListTranscriptPage(
                null, "existing-sync", true, true, 500,
                Utf8("""{"kind":"calendar#events","nextSyncToken":"next","items":[]}"""))],
            TimeZoneInfo.Utc,
            ObservedUtc);
        var broken = new GoogleCalendarEventsListTranscriptSource(
            account,
            [
                Page(null, """{"kind":"calendar#events","nextPageToken":"expected","items":[]}"""),
                Page("wrong", """{"kind":"calendar#events","nextSyncToken":"next","items":[]}"""),
            ],
            TimeZoneInfo.Utc,
            ObservedUtc);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            incremental.ReadSnapshotBatchAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            broken.ReadSnapshotBatchAsync());
    }

    [Theory]
    [InlineData("""{"kind":"calendar#events","nextSyncToken":"s","items":[{"id":"e","etag":"r","status":"confirmed","recurrence":["RRULE:FREQ=DAILY"],"start":{"date":"2027-01-01"},"end":{"date":"2027-01-02"}}]}""")]
    [InlineData("""{"kind":"calendar#events","nextSyncToken":"s","items":[{"id":"e","etag":"r","status":"confirmed","start":{"dateTime":"2027-01-01T09:00:00"},"end":{"dateTime":"2027-01-01T10:00:00"}}]}""")]
    [InlineData("""{"kind":"calendar#events","kind":"calendar#events","nextSyncToken":"s","items":[]}""")]
    public async Task UnsupportedRecurrenceAmbiguousTimeOrDuplicateJsonFailsClosed(string json)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Source(json).ReadSnapshotBatchAsync());
    }

    private static GoogleCalendarEventsListTranscriptSource Source(string response) => new(
        Sha256("google-transcript-account"),
        [Page(null, response)],
        TimeZoneInfo.Utc,
        ObservedUtc);

    private static GoogleCalendarEventsListTranscriptPage Page(
        string? requestPageToken,
        string response) => new(
            requestPageToken,
            RequestSyncToken: null,
            SingleEvents: true,
            ShowDeleted: true,
            MaxResults: 500,
            Utf8(response));

    private static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);

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
