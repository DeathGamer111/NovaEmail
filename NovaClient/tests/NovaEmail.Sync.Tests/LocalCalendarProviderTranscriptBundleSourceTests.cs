using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovaEmail.Storage;

namespace NovaEmail.Sync.Tests;

public sealed class LocalCalendarProviderTranscriptBundleSourceTests
{
    private static readonly DateTimeOffset ObservedUtc =
        DateTimeOffset.UtcNow.AddMinutes(-1);

    [Fact]
    public async Task ExplicitGoogleBundleImportsWithoutRetainingRawTranscriptIdentifiers()
    {
        const string rawSyncToken = "private-google-sync-token";
        const string rawEventId = "private-google-event-id";
        var response = $$"""
            {
              "kind": "calendar#events",
              "nextSyncToken": "{{rawSyncToken}}",
              "items": [
                {
                  "id": "{{rawEventId}}",
                  "etag": "private-google-etag",
                  "status": "confirmed",
                  "summary": "Local transcript review",
                  "start": { "date": "2028-01-10" },
                  "end": { "date": "2028-01-11" }
                }
              ]
            }
            """;
        var source = Source(GoogleBundle(response));

        var batch = await source.ReadSnapshotBatchAsync();

        Assert.Equal(CalendarEventSourceKind.Google, batch.SourceKind);
        Assert.False(batch.IsLocalProtocolDouble);
        Assert.True(batch.IsExplicitLocalFileImport);
        Assert.False(batch.NetworkActionsInvoked);
        Assert.False(batch.ProductionEndpointsInvoked);
        var snapshot = Assert.Single(batch.Snapshots);
        Assert.True(snapshot.AllDay);
        var serialized = JsonSerializer.Serialize(batch);
        Assert.DoesNotContain(rawSyncToken, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(rawEventId, serialized, StringComparison.Ordinal);

        var testRoot = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(testRoot);
            var result = await new LocalCalendarProviderImportCoordinator(store)
                .ImportAsync(source);
            Assert.Equal(1, result.Added);
            Assert.True(result.ExplicitLocalFileRead);
            Assert.False(result.NetworkActionsInvoked);
            Assert.Single(await store.ReadLocalCalendarAgendaAsync(
                new DateTime(2028, 1, 10), new DateTime(2028, 1, 11)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitAppleBundleImportsCalDavResourceWithoutNetwork()
    {
        const string rawHref = "/private/calendar/bundle-event.ics";
        var response = $$"""
            <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:response>
                <D:href>{{rawHref}}</D:href>
                <D:propstat>
                  <D:prop>
                    <D:getetag>private-apple-etag</D:getetag>
                    <C:calendar-data>BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:private-apple-bundle-uid@example.test
            DTSTART:20280214T150000Z
            DTEND:20280214T160000Z
            SUMMARY:Apple transcript review
            END:VEVENT
            END:VCALENDAR
            </C:calendar-data>
                  </D:prop>
                  <D:status>HTTP/1.1 200 OK</D:status>
                </D:propstat>
              </D:response>
              <D:sync-token>urn:novaemail:test:apple-bundle</D:sync-token>
            </D:multistatus>
            """;
        var bundle = $$"""
            {
              "schemaVersion": 1,
              "provider": "Apple",
              "requestSyncToken": null,
              "syncLevel": 1,
              "getEtagRequested": true,
              "calendarDataRequested": true,
              "responseBase64": "{{Convert.ToBase64String(Encoding.UTF8.GetBytes(response))}}"
            }
            """;

        var batch = await Source(bundle).ReadSnapshotBatchAsync();

        Assert.Equal(CalendarEventSourceKind.Apple, batch.SourceKind);
        Assert.Single(batch.Snapshots);
        Assert.Single(batch.SeriesSnapshots!);
        Assert.DoesNotContain(
            rawHref,
            JsonSerializer.Serialize(batch),
            StringComparison.Ordinal);
        Assert.False(batch.NetworkActionsInvoked);
        Assert.False(batch.ProductionEndpointsInvoked);
    }

    [Fact]
    public async Task BundleBytesAreOwnedBeforeCallerMutation()
    {
        var bundle = Encoding.UTF8.GetBytes(GoogleBundle("""
            {
              "kind": "calendar#events",
              "nextSyncToken": "owned-buffer-token",
              "items": []
            }
            """));
        var source = new LocalCalendarProviderTranscriptBundleSource(
            Sha256("owned-bundle-account"),
            bundle,
            TimeZoneInfo.Utc,
            ObservedUtc);
        Array.Fill(bundle, (byte)0);

        var batch = await source.ReadSnapshotBatchAsync();

        Assert.Equal(CalendarEventSourceKind.Google, batch.SourceKind);
        Assert.Empty(batch.Snapshots);
    }

    [Theory]
    [InlineData("""
        {"schemaVersion":1,"provider":"Google","provider":"Google","pages":[]}
        """)]
    [InlineData("""
        {"schemaVersion":1,"provider":"Google","pages":[],"unexpected":true}
        """)]
    [InlineData("""
        {"schemaVersion":1,"provider":"Unknown","pages":[]}
        """)]
    [InlineData("""
        {"schemaVersion":1,"provider":"Apple","requestSyncToken":null,"syncLevel":1,"getEtagRequested":true,"calendarDataRequested":true,"responseBase64":"not base64"}
        """)]
    [InlineData("""
        {"schemaVersion":2,"provider":"Google","pages":[]}
        """)]
    public async Task DuplicateUnknownMalformedAndUnsupportedBundlesFailClosed(string bundle)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Source(bundle).ReadSnapshotBatchAsync());
    }

    [Fact]
    public void OversizedBundleAndAggregateGoogleResponsesFailBeforeParsing()
    {
        Assert.Throws<InvalidDataException>(() => new LocalCalendarProviderTranscriptBundleSource(
            Sha256("oversized-bundle-account"),
            new byte[LocalCalendarProviderTranscriptBundleSource.MaximumBundleBytes + 1],
            TimeZoneInfo.Utc,
            ObservedUtc));

        var pages = Enumerable.Range(0, 9)
            .Select(_ => new GoogleCalendarEventsListTranscriptPage(
                null,
                null,
                true,
                true,
                500,
                new byte[GoogleCalendarEventsListTranscriptSource.MaximumPageBytes]))
            .ToArray();
        Assert.Throws<InvalidDataException>(() => new GoogleCalendarEventsListTranscriptSource(
            Sha256("aggregate-google-account"),
            pages,
            TimeZoneInfo.Utc,
            ObservedUtc));
    }

    [Fact]
    public async Task DisposedBundleCannotBeParsedAgain()
    {
        var source = Source(GoogleBundle("""
            {"kind":"calendar#events","nextSyncToken":"dispose-token","items":[]}
            """));
        source.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            source.ReadSnapshotBatchAsync());
    }

    private static LocalCalendarProviderTranscriptBundleSource Source(string bundle) => new(
        Sha256("local-transcript-bundle-account"),
        Encoding.UTF8.GetBytes(bundle),
        TimeZoneInfo.Utc,
        ObservedUtc);

    private static string GoogleBundle(string response) => $$"""
        {
          "schemaVersion": 1,
          "provider": "Google",
          "pages": [
            {
              "requestPageToken": null,
              "requestSyncToken": null,
              "singleEvents": true,
              "showDeleted": true,
              "maxResults": 500,
              "responseBase64": "{{Convert.ToBase64String(Encoding.UTF8.GetBytes(response))}}"
            }
          ]
        }
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
