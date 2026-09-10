using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NovaEmail.Storage;

namespace NovaEmail.Sync;

/// <summary>
/// One locally captured, read-only CalDAV sync-collection response plus the
/// bounded request shape that produced it. This record has no HTTP or
/// authentication capability and must never contain credentials.
/// </summary>
public sealed record AppleCalDavSyncTranscript(
    string? RequestSyncToken,
    int SyncLevel,
    bool GetEtagRequested,
    bool CalendarDataRequested,
    ReadOnlyMemory<byte> ResponseUtf8);

/// <summary>
/// Converts a complete local Apple CalDAV sync-collection transcript into the
/// append-only provider observation boundary. Each changed CalDAV resource is
/// treated as one complete series domain, including standalone events, so an
/// incremental 404 can retain tombstones without deleting prior history.
/// OAuth, HTTP, provider writes, and raw token/resource persistence are absent.
/// </summary>
public sealed class AppleCalDavSyncTranscriptSource
    : ILocalCalendarProviderSnapshotSource
{
    public const int MaximumResponseBytes = 4 * 1024 * 1024;
    public const int MaximumResources = 500;
    public const int MaximumTokenCharacters = 4096;
    public const int MaximumHrefCharacters = 4096;
    public const int MaximumEtagCharacters = 1024;

    private const int MaximumXmlDepth = 16;
    private const int MaximumXmlElements = 5000;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    private readonly string _providerAccountIdentitySha256;
    private readonly AppleCalDavSyncTranscript _transcript;
    private readonly TimeZoneInfo _displayTimeZone;
    private readonly DateTimeOffset _observedUtc;

    public AppleCalDavSyncTranscriptSource(
        string providerAccountIdentitySha256,
        AppleCalDavSyncTranscript transcript,
        TimeZoneInfo displayTimeZone,
        DateTimeOffset observedUtc)
    {
        ValidateLowercaseSha256(
            providerAccountIdentitySha256,
            nameof(providerAccountIdentitySha256));
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(displayTimeZone);
        if (observedUtc.Offset != TimeSpan.Zero ||
            observedUtc < DateTimeOffset.UnixEpoch ||
            observedUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new InvalidDataException("The Apple CalDAV observation time is invalid.");
        }

        _providerAccountIdentitySha256 = providerAccountIdentitySha256;
        _transcript = transcript with
        {
            ResponseUtf8 = transcript.ResponseUtf8.ToArray(),
        };
        _displayTimeZone = displayTimeZone;
        _observedUtc = observedUtc;
    }

    public async Task<LocalCalendarProviderSnapshotBatch> ReadSnapshotBatchAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequestAttestation();
        var root = ParseBoundedXml();
        var responseToken = ReadResponseSyncToken(root);
        var incremental = _transcript.RequestSyncToken is not null;
        var snapshots = new List<CalendarProviderEventSnapshotImport>();
        var seriesSnapshots = new List<CalendarProviderSeriesSnapshotImport>();
        var resourceIdentities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var response in root.Root!.Elements(Dav + "response"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seriesSnapshots.Count >= MaximumResources)
                throw new InvalidDataException(
                    "The Apple CalDAV transcript exceeds the 500-resource limit.");

            var resource = ParseResourceEnvelope(response, incremental);
            var resourceIdentity = HashIdentity(string.Concat(
                "Apple CalDAV resource\0",
                _providerAccountIdentitySha256,
                "\0",
                resource.Href));
            if (!resourceIdentities.Add(resourceIdentity))
                throw new InvalidDataException(
                    "The Apple CalDAV transcript contains a duplicate resource href.");

            if (resource.IsDeleted)
            {
                seriesSnapshots.Add(new CalendarProviderSeriesSnapshotImport(
                    CalendarEventSourceKind.Apple,
                    _providerAccountIdentitySha256,
                    resourceIdentity,
                    HashIdentity(string.Concat(
                        "Apple CalDAV deleted resource revision\0",
                        resourceIdentity,
                        "\0",
                        responseToken)),
                    [],
                    _observedUtc));
                continue;
            }

            var parsed = await new LocalIcsCalendarProviderSnapshotSource(
                    CalendarEventSourceKind.Apple,
                    _providerAccountIdentitySha256,
                    resource.CalendarDataUtf8.Span,
                    _observedUtc,
                    _displayTimeZone)
                .ReadSnapshotBatchAsync(cancellationToken)
                .ConfigureAwait(false);
            ValidateSingleResourceDomain(parsed);

            var members = new List<string>();
            foreach (var inner in parsed.Snapshots)
            {
                if (snapshots.Count >= LocalCalendarProviderImportCoordinator.MaximumSnapshotsPerBatch)
                    throw new InvalidDataException(
                        "The Apple CalDAV transcript exceeds the 500-event limit.");
                var eventIdentity = HashIdentity(string.Concat(
                    "Apple CalDAV event\0",
                    resourceIdentity,
                    "\0",
                    inner.ProviderEventIdentitySha256));
                var revisionIdentity = HashIdentity(string.Concat(
                    "Apple CalDAV event revision\0",
                    resourceIdentity,
                    "\0",
                    resource.Etag,
                    "\0",
                    inner.ProviderRevisionIdentitySha256,
                    "\0",
                    _displayTimeZone.Id));
                snapshots.Add(inner with
                {
                    ProviderEventIdentitySha256 = eventIdentity,
                    ProviderRevisionIdentitySha256 = revisionIdentity,
                    ProviderSeriesIdentitySha256 = resourceIdentity,
                });
                if (!inner.IsDeleted) members.Add(eventIdentity);
            }

            members.Sort(StringComparer.Ordinal);
            seriesSnapshots.Add(new CalendarProviderSeriesSnapshotImport(
                CalendarEventSourceKind.Apple,
                _providerAccountIdentitySha256,
                resourceIdentity,
                HashIdentity(string.Concat(
                    "Apple CalDAV resource revision\0",
                    resourceIdentity,
                    "\0",
                    resource.Etag,
                    "\0",
                    responseToken,
                    "\0",
                    string.Join("\0", members),
                    "\0",
                    _displayTimeZone.Id)),
                members,
                _observedUtc));
        }

        return new LocalCalendarProviderSnapshotBatch(
            CalendarEventSourceKind.Apple,
            _providerAccountIdentitySha256,
            snapshots,
            IsLocalProtocolDouble: true,
            NetworkActionsInvoked: false,
            ProductionEndpointsInvoked: false,
            IsExplicitLocalFileImport: false,
            SeriesSnapshots: seriesSnapshots);
    }

    private void ValidateRequestAttestation()
    {
        if (_transcript.SyncLevel != 1 ||
            !_transcript.GetEtagRequested ||
            !_transcript.CalendarDataRequested)
        {
            throw new InvalidDataException(
                "The Apple CalDAV transcript must attest a depth-1 sync requesting getetag and calendar-data.");
        }
        if (_transcript.RequestSyncToken is not null)
            ValidateSyncToken(_transcript.RequestSyncToken, "request sync token");
        if (_transcript.ResponseUtf8.Length is < 16 or > MaximumResponseBytes)
            throw new InvalidDataException(
                "The Apple CalDAV response is empty or exceeds 4 MiB.");
    }

    private XDocument ParseBoundedXml()
    {
        string xml;
        try
        {
            xml = StrictUtf8.GetString(_transcript.ResponseUtf8.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The Apple CalDAV response is not strict UTF-8.", exception);
        }
        if (xml.Length > 0 && xml[0] == '\uFEFF') xml = xml[1..];
        if (xml.Contains('\0'))
            throw new InvalidDataException("The Apple CalDAV response contains a null character.");

        XDocument document;
        try
        {
            using var stringReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumResponseBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                IgnoreWhitespace = true,
            });
            document = XDocument.Load(xmlReader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                "The Apple CalDAV response is not strict bounded XML.", exception);
        }

        if (document.Root?.Name != Dav + "multistatus" ||
            document.Nodes().Count() != 1 ||
            document.DescendantNodes().Any(node => node is XComment or XProcessingInstruction))
        {
            throw new InvalidDataException(
                "The Apple CalDAV response must contain one DAV:multistatus document.");
        }
        var elements = document.Descendants().ToArray();
        if (elements.Length > MaximumXmlElements ||
            elements.Any(element => element.Ancestors().Count() > MaximumXmlDepth))
        {
            throw new InvalidDataException(
                "The Apple CalDAV response exceeds its XML structure limits.");
        }
        foreach (var child in document.Root.Elements())
        {
            if (child.Name != Dav + "response" && child.Name != Dav + "sync-token")
                throw new InvalidDataException(
                    "The Apple CalDAV multistatus contains an unsupported element.");
        }
        return document;
    }

    private static string ReadResponseSyncToken(XDocument document)
    {
        var tokens = document.Root!.Elements(Dav + "sync-token").ToArray();
        if (tokens.Length != 1 || tokens[0].HasElements)
            throw new InvalidDataException(
                "The Apple CalDAV response must contain exactly one sync-token.");
        var token = tokens[0].Value;
        ValidateSyncToken(token, "response sync token");
        return token;
    }

    private static ResourceEnvelope ParseResourceEnvelope(XElement response, bool incremental)
    {
        RequireOnlyChildren(response, Dav + "href", Dav + "status", Dav + "propstat");
        var hrefElement = RequireSingle(response, Dav + "href", "CalDAV response href");
        if (hrefElement.HasElements)
            throw new InvalidDataException("A CalDAV response href cannot contain markup.");
        var href = hrefElement.Value;
        ValidateHref(href);

        var directStatuses = response.Elements(Dav + "status").ToArray();
        var propstats = response.Elements(Dav + "propstat").ToArray();
        if (directStatuses.Length == 1 && propstats.Length == 0)
        {
            if (directStatuses[0].HasElements ||
                !string.Equals(
                    directStatuses[0].Value,
                    "HTTP/1.1 404 Not Found",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Only an incremental HTTP/1.1 404 Not Found resource status is supported.");
            }
            if (!incremental)
                throw new InvalidDataException(
                    "An initial Apple CalDAV sync cannot contain a deleted resource response.");
            return new ResourceEnvelope(href, string.Empty, ReadOnlyMemory<byte>.Empty, true);
        }
        if (directStatuses.Length != 0 || propstats.Length != 1)
            throw new InvalidDataException(
                "A CalDAV resource must contain exactly one active propstat or one deletion status.");

        var propstat = propstats[0];
        RequireOnlyChildren(propstat, Dav + "prop", Dav + "status");
        var status = RequireSingle(propstat, Dav + "status", "CalDAV propstat status");
        if (status.HasElements ||
            !string.Equals(status.Value, "HTTP/1.1 200 OK", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Apple CalDAV transcript is incomplete or contains a failed property status.");
        }
        var property = RequireSingle(propstat, Dav + "prop", "CalDAV propstat properties");
        RequireOnlyChildren(property, Dav + "getetag", CalDav + "calendar-data");
        var etagElement = RequireSingle(property, Dav + "getetag", "CalDAV getetag");
        var calendarDataElement = RequireSingle(
            property, CalDav + "calendar-data", "CalDAV calendar-data");
        if (etagElement.HasElements || calendarDataElement.HasElements)
            throw new InvalidDataException(
                "CalDAV getetag and calendar-data values cannot contain markup.");
        var etag = etagElement.Value;
        if (etag.Length is < 1 or > MaximumEtagCharacters ||
            etag.Any(char.IsControl))
        {
            throw new InvalidDataException("The CalDAV getetag value is invalid.");
        }
        var calendarData = StrictUtf8.GetBytes(calendarDataElement.Value);
        if (calendarData.Length is < 1 or > LocalIcsCalendarProviderSnapshotSource.MaximumDocumentBytes)
            throw new InvalidDataException("The CalDAV calendar-data value is empty or too large.");
        return new ResourceEnvelope(href, etag, calendarData, false);
    }

    private static void ValidateSingleResourceDomain(LocalCalendarProviderSnapshotBatch parsed)
    {
        if (parsed.SourceKind != CalendarEventSourceKind.Apple ||
            parsed.NetworkActionsInvoked ||
            parsed.ProductionEndpointsInvoked ||
            !parsed.IsExplicitLocalFileImport ||
            parsed.IsLocalProtocolDouble ||
            parsed.Snapshots.Count == 0)
        {
            throw new InvalidDataException(
                "The CalDAV calendar-data parser returned an unsafe or empty resource.");
        }
        var innerSeries = parsed.SeriesSnapshots ?? [];
        if (innerSeries.Count > 1 ||
            (innerSeries.Count == 0 && parsed.Snapshots.Count != 1) ||
            (innerSeries.Count == 1 && parsed.Snapshots.Any(
                snapshot => !string.Equals(
                    snapshot.ProviderSeriesIdentitySha256,
                    innerSeries[0].ProviderSeriesIdentitySha256,
                    StringComparison.Ordinal))))
        {
            throw new InvalidDataException(
                "One CalDAV resource must contain exactly one standalone event or recurring UID domain.");
        }
    }

    private static XElement RequireSingle(XElement parent, XName name, string label)
    {
        var matches = parent.Elements(name).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"The {label} must occur exactly once.");
        return matches[0];
    }

    private static void RequireOnlyChildren(XElement parent, params XName[] allowed)
    {
        foreach (var node in parent.Nodes())
        {
            if (node is XText text && string.IsNullOrWhiteSpace(text.Value)) continue;
            if (node is not XElement child || !allowed.Contains(child.Name))
                throw new InvalidDataException(
                    $"The Apple CalDAV element {parent.Name} contains an unsupported child.");
        }
    }

    private static void ValidateSyncToken(string token, string label)
    {
        if (token.Length is < 1 or > MaximumTokenCharacters ||
            token.Any(char.IsWhiteSpace) ||
            token.Any(char.IsControl) ||
            !Uri.TryCreate(token, UriKind.Absolute, out var uri) ||
            uri.IsFile)
        {
            throw new InvalidDataException($"The Apple CalDAV {label} is not a bounded opaque URI.");
        }
    }

    private static void ValidateHref(string href)
    {
        if (href.Length is < 1 or > MaximumHrefCharacters ||
            href.Any(char.IsWhiteSpace) ||
            href.Any(char.IsControl) ||
            href.Contains('\\') ||
            href.EndsWith('/') ||
            !Uri.TryCreate(href, UriKind.RelativeOrAbsolute, out var uri) ||
            (uri.IsAbsoluteUri && uri.Scheme is not ("http" or "https")) ||
            (!uri.IsAbsoluteUri && !href.StartsWith('/')))
        {
            throw new InvalidDataException("The Apple CalDAV resource href is invalid.");
        }
    }

    private static string HashIdentity(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateLowercaseSha256(string value, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value, fieldName);
        if (value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"{fieldName} must be a lowercase SHA-256 value.");
        }
    }

    private sealed record ResourceEnvelope(
        string Href,
        string Etag,
        ReadOnlyMemory<byte> CalendarDataUtf8,
        bool IsDeleted);
}
