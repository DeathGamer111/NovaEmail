using System.Security.Cryptography;
using System.Text.Json;
using NovaEmail.Storage;

namespace NovaEmail.Sync;

/// <summary>
/// Parses one explicitly selected local JSON envelope containing already captured
/// Google events.list or Apple CalDAV request/response transcript bytes. It owns
/// no file-system, HTTP, OAuth, credential, token-store, provider-write, or
/// external-launch capability.
/// </summary>
public sealed class LocalCalendarProviderTranscriptBundleSource
    : ILocalCalendarProviderSnapshotSource, IDisposable
{
    public const int MaximumBundleBytes = 12 * 1024 * 1024;
    public const int SchemaVersion = 1;

    private readonly string _providerAccountIdentitySha256;
    private readonly byte[] _bundleUtf8;
    private readonly TimeZoneInfo _displayTimeZone;
    private readonly DateTimeOffset _observedUtc;
    private bool _disposed;

    public LocalCalendarProviderTranscriptBundleSource(
        string providerAccountIdentitySha256,
        ReadOnlySpan<byte> bundleUtf8,
        TimeZoneInfo displayTimeZone,
        DateTimeOffset observedUtc)
    {
        ValidateLowercaseSha256(providerAccountIdentitySha256);
        ArgumentNullException.ThrowIfNull(displayTimeZone);
        if (bundleUtf8.IsEmpty || bundleUtf8.Length > MaximumBundleBytes)
            throw new InvalidDataException(
                "The selected calendar transcript bundle is empty or exceeds 12 MiB.");
        if (observedUtc.Offset != TimeSpan.Zero ||
            observedUtc < DateTimeOffset.UnixEpoch ||
            observedUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new InvalidDataException(
                "The local calendar transcript observation time is invalid.");
        }

        _providerAccountIdentitySha256 = providerAccountIdentitySha256;
        _bundleUtf8 = bundleUtf8.ToArray();
        _displayTimeZone = displayTimeZone;
        _observedUtc = observedUtc;
    }

    public async Task<LocalCalendarProviderSnapshotBatch> ReadSnapshotBatchAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        using var document = ParseStrictJson();
        var root = document.RootElement;
        RequireObject(root, "calendar transcript bundle");
        RequireUniqueProperties(root, "calendar transcript bundle", 32);
        if (RequiredInt32(root, "schemaVersion") != SchemaVersion)
            throw new InvalidDataException(
                "The calendar transcript bundle schema version is unsupported.");
        var provider = RequiredString(root, "provider", 16);
        ILocalCalendarProviderSnapshotSource inner = provider switch
        {
            "Google" => ParseGoogle(root),
            "Apple" => ParseApple(root),
            _ => throw new InvalidDataException(
                "The calendar transcript bundle provider must be Google or Apple."),
        };
        var batch = await inner.ReadSnapshotBatchAsync(cancellationToken).ConfigureAwait(false);
        if (!batch.IsLocalProtocolDouble ||
            batch.IsExplicitLocalFileImport ||
            batch.NetworkActionsInvoked ||
            batch.ProductionEndpointsInvoked)
        {
            throw new InvalidDataException(
                "The calendar transcript parser returned unsafe local-origin provenance.");
        }
        return batch with
        {
            IsLocalProtocolDouble = false,
            IsExplicitLocalFileImport = true,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_bundleUtf8);
        _disposed = true;
    }

    private JsonDocument ParseStrictJson()
    {
        try
        {
            return JsonDocument.Parse(_bundleUtf8, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 24,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The selected calendar transcript bundle is not strict bounded UTF-8 JSON.",
                exception);
        }
    }

    private GoogleCalendarEventsListTranscriptSource ParseGoogle(JsonElement root)
    {
        RequireExactProperties(
            root,
            "Google calendar transcript bundle",
            "schemaVersion", "provider", "pages");
        if (!root.TryGetProperty("pages", out var pagesElement) ||
            pagesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The Google calendar transcript bundle pages array is missing.");
        }
        var pageCount = pagesElement.GetArrayLength();
        if (pageCount is < 1 or > GoogleCalendarEventsListTranscriptSource.MaximumPages)
            throw new InvalidDataException(
                "The Google calendar transcript bundle page count is invalid.");

        var pages = new GoogleCalendarEventsListTranscriptPage[pageCount];
        var index = 0;
        foreach (var page in pagesElement.EnumerateArray())
        {
            RequireObject(page, "Google transcript page");
            RequireUniqueProperties(page, "Google transcript page", 16);
            RequireExactProperties(
                page,
                "Google transcript page",
                "requestPageToken", "requestSyncToken", "singleEvents",
                "showDeleted", "maxResults", "responseBase64");
            pages[index++] = new GoogleCalendarEventsListTranscriptPage(
                RequiredNullableString(
                    page,
                    "requestPageToken",
                    GoogleCalendarEventsListTranscriptSource.MaximumTokenCharacters),
                RequiredNullableString(
                    page,
                    "requestSyncToken",
                    GoogleCalendarEventsListTranscriptSource.MaximumTokenCharacters),
                RequiredBoolean(page, "singleEvents"),
                RequiredBoolean(page, "showDeleted"),
                RequiredInt32(page, "maxResults"),
                DecodeCanonicalBase64(
                    page,
                    "responseBase64",
                    GoogleCalendarEventsListTranscriptSource.MaximumPageBytes,
                    "Google response page"));
        }

        return new GoogleCalendarEventsListTranscriptSource(
            _providerAccountIdentitySha256,
            pages,
            _displayTimeZone,
            _observedUtc);
    }

    private AppleCalDavSyncTranscriptSource ParseApple(JsonElement root)
    {
        RequireExactProperties(
            root,
            "Apple calendar transcript bundle",
            "schemaVersion", "provider", "requestSyncToken", "syncLevel",
            "getEtagRequested", "calendarDataRequested", "responseBase64");
        var transcript = new AppleCalDavSyncTranscript(
            RequiredNullableString(
                root,
                "requestSyncToken",
                AppleCalDavSyncTranscriptSource.MaximumTokenCharacters),
            RequiredInt32(root, "syncLevel"),
            RequiredBoolean(root, "getEtagRequested"),
            RequiredBoolean(root, "calendarDataRequested"),
            DecodeCanonicalBase64(
                root,
                "responseBase64",
                AppleCalDavSyncTranscriptSource.MaximumResponseBytes,
                "Apple CalDAV response"));
        return new AppleCalDavSyncTranscriptSource(
            _providerAccountIdentitySha256,
            transcript,
            _displayTimeZone,
            _observedUtc);
    }

    private static byte[] DecodeCanonicalBase64(
        JsonElement parent,
        string propertyName,
        int maximumDecodedBytes,
        string label)
    {
        var encoded = RequiredString(
            parent,
            propertyName,
            checked(4 * ((maximumDecodedBytes + 2) / 3)));
        if (encoded.Any(char.IsWhiteSpace) || encoded.Any(char.IsControl))
            throw new InvalidDataException($"The {label} Base64 value is not canonical.");
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"The {label} Base64 value is invalid.", exception);
        }
        if (decoded.Length is < 1 || decoded.Length > maximumDecodedBytes ||
            !string.Equals(Convert.ToBase64String(decoded), encoded, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {label} bytes are empty, oversized, or noncanonical.");
        }
        return decoded;
    }

    private static void RequireExactProperties(
        JsonElement value,
        string label,
        params string[] expected)
    {
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length ||
            actual.Except(expected, StringComparer.Ordinal).Any() ||
            expected.Except(actual, StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException($"The {label} has an unsupported or incomplete schema.");
        }
    }

    private static void RequireObject(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"The {label} must be a JSON object.");
    }

    private static void RequireUniqueProperties(
        JsonElement value,
        string label,
        int maximumProperties)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (++count > maximumProperties || !names.Add(property.Name))
                throw new InvalidDataException(
                    $"The {label} has duplicate or excessive JSON properties.");
        }
    }

    private static string RequiredString(
        JsonElement parent,
        string propertyName,
        int maximumCharacters)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"The {propertyName} string is missing.");
        }
        var result = value.GetString()!;
        if (result.Length is < 1 || result.Length > maximumCharacters ||
            result.Any(char.IsControl))
        {
            throw new InvalidDataException($"The {propertyName} string is invalid.");
        }
        return result;
    }

    private static string? RequiredNullableString(
        JsonElement parent,
        string propertyName,
        int maximumCharacters)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
            throw new InvalidDataException($"The {propertyName} value is missing.");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"The {propertyName} value is invalid.");
        var result = value.GetString()!;
        if (result.Length is < 1 || result.Length > maximumCharacters ||
            result.Any(char.IsControl))
        {
            throw new InvalidDataException($"The {propertyName} value is invalid.");
        }
        return result;
    }

    private static bool RequiredBoolean(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"The {propertyName} Boolean is missing.");
        }
        return value.GetBoolean();
    }

    private static int RequiredInt32(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"The {propertyName} integer is missing or invalid.");
        }
        return result;
    }

    private static void ValidateLowercaseSha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "The provider account identity must be a lowercase SHA-256 value.");
        }
    }
}
