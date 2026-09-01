using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NovaEmail.Storage;

namespace NovaEmail.Sync;

/// <summary>
/// One locally captured Google Calendar events.list response and the bounded,
/// non-secret request shape that produced it. This type cannot issue HTTP requests.
/// </summary>
public sealed record GoogleCalendarEventsListTranscriptPage(
    string? RequestPageToken,
    string? RequestSyncToken,
    bool SingleEvents,
    bool ShowDeleted,
    int MaxResults,
    ReadOnlyMemory<byte> ResponseUtf8);

/// <summary>
/// Converts a complete, paged Google events.list transcript into the existing
/// append-only provider snapshot boundary. Only a full singleEvents/showDeleted
/// transcript is accepted because incremental pages cannot prove complete recurring
/// series membership. OAuth, HTTP, provider writes, and token persistence are absent.
/// </summary>
public sealed partial class GoogleCalendarEventsListTranscriptSource
    : ILocalCalendarProviderSnapshotSource
{
    public const int MaximumPages = 16;
    public const int MaximumPageBytes = 1024 * 1024;
    public const int MaximumAggregateResponseBytes = 8 * 1024 * 1024;
    public const int MaximumTokenCharacters = 4096;

    private readonly string _providerAccountIdentitySha256;
    private readonly GoogleCalendarEventsListTranscriptPage[] _pages;
    private readonly TimeZoneInfo _displayTimeZone;
    private readonly DateTimeOffset _observedUtc;

    public GoogleCalendarEventsListTranscriptSource(
        string providerAccountIdentitySha256,
        IReadOnlyList<GoogleCalendarEventsListTranscriptPage> pages,
        TimeZoneInfo displayTimeZone,
        DateTimeOffset observedUtc)
    {
        ValidateLowercaseSha256(
            providerAccountIdentitySha256,
            nameof(providerAccountIdentitySha256));
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(displayTimeZone);
        if (pages.Count is < 1 or > MaximumPages)
            throw new InvalidDataException("The Google Calendar transcript page count is invalid.");
        if (observedUtc == default || observedUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("The Google Calendar observation time is invalid.");

        var aggregateBytes = 0;
        var ownedPages = new GoogleCalendarEventsListTranscriptPage[pages.Count];
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index] ??
                throw new InvalidDataException("The Google Calendar transcript contains a null page.");
            if (page.ResponseUtf8.Length is < 2 or > MaximumPageBytes)
                throw new InvalidDataException(
                    "A Google Calendar response page is empty or exceeds 1 MiB.");
            aggregateBytes = checked(aggregateBytes + page.ResponseUtf8.Length);
            if (aggregateBytes > MaximumAggregateResponseBytes)
                throw new InvalidDataException(
                    "The Google Calendar transcript exceeds the 8 MiB aggregate response limit.");
            ownedPages[index] = page with { ResponseUtf8 = page.ResponseUtf8.ToArray() };
        }

        _providerAccountIdentitySha256 = providerAccountIdentitySha256;
        _pages = ownedPages;
        _displayTimeZone = displayTimeZone;
        _observedUtc = observedUtc;
    }

    public Task<LocalCalendarProviderSnapshotBatch> ReadSnapshotBatchAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = new List<CalendarProviderEventSnapshotImport>();
        string? expectedPageToken = null;
        string? finalSyncToken = null;

        for (var pageIndex = 0; pageIndex < _pages.Length; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = _pages[pageIndex]
                ?? throw new InvalidDataException("The Google Calendar transcript contains a null page.");
            ValidateRequestAttestation(page, expectedPageToken);
            if (page.ResponseUtf8.Length is < 2 or > MaximumPageBytes)
                throw new InvalidDataException("A Google Calendar response page is empty or exceeds 1 MiB.");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(page.ResponseUtf8, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "A Google Calendar response page is not strict bounded JSON.", exception);
            }

            using (document)
            {
                var root = document.RootElement;
                RequireObject(root, "Google Calendar response");
                RequireUniqueProperties(root, "Google Calendar response", 64);
                if (RequiredString(root, "kind", 128) != "calendar#events")
                    throw new InvalidDataException("The Google Calendar response kind is invalid.");

                var nextPageToken = OptionalString(root, "nextPageToken", MaximumTokenCharacters);
                var nextSyncToken = OptionalString(root, "nextSyncToken", MaximumTokenCharacters);
                var isLastPage = pageIndex == _pages.Length - 1;
                if (isLastPage)
                {
                    if (nextPageToken is not null || nextSyncToken is null)
                        throw new InvalidDataException(
                            "The final Google Calendar page must contain exactly one nextSyncToken.");
                    finalSyncToken = nextSyncToken;
                }
                else if (nextPageToken is null || nextSyncToken is not null)
                {
                    throw new InvalidDataException(
                        "An intermediate Google Calendar page must contain exactly one nextPageToken.");
                }

                if (!root.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The Google Calendar response items array is missing.");
                foreach (var item in items.EnumerateArray())
                {
                    if (snapshots.Count >= LocalCalendarProviderImportCoordinator.MaximumSnapshotsPerBatch)
                        throw new InvalidDataException(
                            "The Google Calendar transcript exceeds the 500-event limit.");
                    snapshots.Add(ParseEvent(item));
                }
                expectedPageToken = nextPageToken;
            }
        }

        if (finalSyncToken is null)
            throw new InvalidDataException("The Google Calendar transcript has no final sync token.");
        var seriesSnapshots = BuildSeriesSnapshots(snapshots, finalSyncToken);
        return Task.FromResult(new LocalCalendarProviderSnapshotBatch(
            CalendarEventSourceKind.Google,
            _providerAccountIdentitySha256,
            snapshots,
            IsLocalProtocolDouble: true,
            NetworkActionsInvoked: false,
            ProductionEndpointsInvoked: false,
            IsExplicitLocalFileImport: false,
            SeriesSnapshots: seriesSnapshots));
    }

    private CalendarProviderEventSnapshotImport ParseEvent(JsonElement item)
    {
        RequireObject(item, "Google Calendar event");
        RequireUniqueProperties(item, "Google Calendar event", 128);
        var id = RequiredString(item, "id", 1024);
        var etag = RequiredString(item, "etag", 1024);
        var status = RequiredString(item, "status", 32);
        if (status is not ("confirmed" or "tentative" or "cancelled"))
            throw new InvalidDataException("The Google Calendar event status is unsupported.");
        if (item.TryGetProperty("recurrence", out var recurrence) &&
            recurrence.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException(
                "The Google Calendar transcript must contain expanded instances, not recurrence rules.");
        var eventType = OptionalString(item, "eventType", 64);
        if (eventType is not null and not "default")
            throw new InvalidDataException("The Google Calendar event type is unsupported.");

        var recurringEventId = OptionalString(item, "recurringEventId", 1024);
        var hasOriginalStart = item.TryGetProperty("originalStartTime", out var originalStart) &&
            originalStart.ValueKind != JsonValueKind.Null;
        if ((recurringEventId is null) != !hasOriginalStart)
            throw new InvalidDataException(
                "A Google recurring instance requires both recurringEventId and originalStartTime.");
        var originalIdentity = hasOriginalStart
            ? ParseIdentityTime(originalStart, "Google original start")
            : string.Empty;
        var eventIdentity = HashIdentity(string.Concat(
            "Google event\0", _providerAccountIdentitySha256, "\0", id, "\0", originalIdentity));
        var revisionIdentity = HashIdentity(string.Concat(
            "Google event revision\0",
            _providerAccountIdentitySha256,
            "\0",
            id,
            "\0",
            etag,
            "\0",
            _displayTimeZone.Id));
        var seriesIdentity = recurringEventId is null
            ? null
            : HashIdentity(string.Concat(
                "Google series\0", _providerAccountIdentitySha256, "\0", recurringEventId));
        var deleted = status == "cancelled";

        if (deleted)
        {
            return new CalendarProviderEventSnapshotImport(
                CalendarEventSourceKind.Google,
                _providerAccountIdentitySha256,
                eventIdentity,
                revisionIdentity,
                Title: string.Empty,
                StartLocal: default,
                EndLocal: default,
                AllDay: false,
                Location: string.Empty,
                Notes: string.Empty,
                IsDeleted: true,
                _observedUtc,
                seriesIdentity);
        }

        if (!item.TryGetProperty("start", out var start) ||
            !item.TryGetProperty("end", out var end))
            throw new InvalidDataException("A Google Calendar event is missing its time range.");
        var range = ParseRange(start, end);
        var title = OptionalString(item, "summary", 512) ?? "Untitled calendar event";
        var location = OptionalString(item, "location", 2048) ?? string.Empty;
        var notes = OptionalString(item, "description", 16384) ?? string.Empty;
        return new CalendarProviderEventSnapshotImport(
            CalendarEventSourceKind.Google,
            _providerAccountIdentitySha256,
            eventIdentity,
            revisionIdentity,
            title,
            range.StartLocal,
            range.EndLocal,
            range.AllDay,
            location,
            notes,
            IsDeleted: false,
            _observedUtc,
            seriesIdentity);
    }

    private CalendarProviderSeriesSnapshotImport[] BuildSeriesSnapshots(
        IReadOnlyList<CalendarProviderEventSnapshotImport> snapshots,
        string finalSyncToken)
    {
        var groups = snapshots
            .Where(snapshot => snapshot.ProviderSeriesIdentitySha256 is not null)
            .GroupBy(snapshot => snapshot.ProviderSeriesIdentitySha256!, StringComparer.Ordinal);
        var result = new List<CalendarProviderSeriesSnapshotImport>();
        foreach (var group in groups)
        {
            var activeMembers = group
                .Where(snapshot => !snapshot.IsDeleted)
                .Select(snapshot => snapshot.ProviderEventIdentitySha256)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var revisionIdentity = HashIdentity(string.Concat(
                "Google series snapshot\0",
                _providerAccountIdentitySha256,
                "\0",
                group.Key,
                "\0",
                finalSyncToken,
                "\0",
                string.Join("\0", activeMembers)));
            result.Add(new CalendarProviderSeriesSnapshotImport(
                CalendarEventSourceKind.Google,
                _providerAccountIdentitySha256,
                group.Key,
                revisionIdentity,
                activeMembers,
                _observedUtc));
        }
        return result.OrderBy(snapshot => snapshot.ProviderSeriesIdentitySha256, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateRequestAttestation(
        GoogleCalendarEventsListTranscriptPage page,
        string? expectedPageToken)
    {
        if (!page.SingleEvents || !page.ShowDeleted || page.RequestSyncToken is not null ||
            page.MaxResults is < 1 or > LocalCalendarProviderImportCoordinator.MaximumSnapshotsPerBatch ||
            !string.Equals(page.RequestPageToken, expectedPageToken, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The Google Calendar transcript is not a complete expanded read-only snapshot.");
        ValidateOptionalToken(page.RequestPageToken, nameof(page.RequestPageToken));
    }

    private (DateTime StartLocal, DateTime EndLocal, bool AllDay) ParseRange(
        JsonElement start,
        JsonElement end)
    {
        RequireObject(start, "Google Calendar start");
        RequireObject(end, "Google Calendar end");
        RequireUniqueProperties(start, "Google Calendar start", 8);
        RequireUniqueProperties(end, "Google Calendar end", 8);
        var startDate = OptionalString(start, "date", 10);
        var endDate = OptionalString(end, "date", 10);
        var startDateTime = OptionalString(start, "dateTime", 64);
        var endDateTime = OptionalString(end, "dateTime", 64);
        if (startDate is not null || endDate is not null)
        {
            if (startDate is null || endDate is null ||
                startDateTime is not null || endDateTime is not null)
                throw new InvalidDataException("A Google all-day event has a mixed time representation.");
            var startLocal = ParseDate(startDate, "Google all-day start");
            var endLocal = ParseDate(endDate, "Google all-day end");
            return (startLocal, endLocal, true);
        }
        if (startDateTime is null || endDateTime is null)
            throw new InvalidDataException("A Google timed event has an incomplete time range.");
        return (
            ConvertToDisplayLocal(startDateTime, "Google timed start"),
            ConvertToDisplayLocal(endDateTime, "Google timed end"),
            false);
    }

    private static string ParseIdentityTime(JsonElement value, string label)
    {
        RequireObject(value, label);
        RequireUniqueProperties(value, label, 8);
        var date = OptionalString(value, "date", 10);
        var dateTime = OptionalString(value, "dateTime", 64);
        if ((date is null) == (dateTime is null))
            throw new InvalidDataException($"{label} must contain exactly one date representation.");
        return date is not null
            ? ParseDate(date, label).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : ParseOffsetTimestamp(dateTime!, label).UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                CultureInfo.InvariantCulture);
    }

    private DateTime ConvertToDisplayLocal(string value, string label)
    {
        var parsed = ParseOffsetTimestamp(value, label);
        var local = TimeZoneInfo.ConvertTime(parsed, _displayTimeZone).DateTime;
        return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
    }

    private static DateTimeOffset ParseOffsetTimestamp(string value, string label)
    {
        if (!ExplicitOffsetPattern().IsMatch(value) ||
            !DateTimeOffset.TryParseExact(
                value,
                ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
            throw new InvalidDataException($"{label} must be an RFC 3339 timestamp with an explicit offset.");
        return parsed;
    }

    private static DateTime ParseDate(string value, string label)
    {
        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
            throw new InvalidDataException($"{label} is not an ISO calendar date.");
        return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
    }

    private static void RequireObject(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{label} is not an object.");
    }

    private static void RequireUniqueProperties(JsonElement value, string label, int maximum)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (++count > maximum || !names.Add(property.Name))
                throw new InvalidDataException($"{label} has duplicate or excessive properties.");
        }
    }

    private static string RequiredString(JsonElement value, string name, int maximumCharacters) =>
        OptionalString(value, name, maximumCharacters)
        ?? throw new InvalidDataException($"Google Calendar field {name} is missing.");

    private static string? OptionalString(JsonElement value, string name, int maximumCharacters)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Google Calendar field {name} is not text.");
        var text = property.GetString()
            ?? throw new InvalidDataException($"Google Calendar field {name} is empty.");
        if (text.Length is < 1 || text.Length > maximumCharacters ||
            text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            throw new InvalidDataException($"Google Calendar field {name} is invalid or oversized.");
        return text;
    }

    private static void ValidateOptionalToken(string? token, string fieldName)
    {
        if (token is not null &&
            (token.Length is < 1 or > MaximumTokenCharacters || token.Any(char.IsControl)))
            throw new InvalidDataException($"{fieldName} is invalid or oversized.");
    }

    private static string HashIdentity(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateLowercaseSha256(string value, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value, fieldName);
        if (value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
            throw new InvalidDataException($"{fieldName} must be a lowercase SHA-256 value.");
    }

    [GeneratedRegex(@"(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOffsetPattern();
}
