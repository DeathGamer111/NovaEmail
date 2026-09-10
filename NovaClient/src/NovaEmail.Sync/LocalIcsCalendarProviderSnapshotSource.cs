using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NovaEmail.Storage;

namespace NovaEmail.Sync;

/// <summary>
/// Parses one explicitly selected, bounded local iCalendar file into the existing
/// append-only Google/Apple provider observation boundary. It has no file-system,
/// OAuth, HTTP, provider-write, or external-launch capability.
/// </summary>
public sealed class LocalIcsCalendarProviderSnapshotSource : ILocalCalendarProviderSnapshotSource
{
    public const int MaximumDocumentBytes = 1_048_576;
    public const int MaximumPhysicalLines = 20_000;
    public const int MaximumUnfoldedLineUtf8Bytes = 32_768;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Regex DurationPattern = new(
        "^P(?:(?<weeks>[0-9]+)W|(?:(?<days>[0-9]+)D)?(?:T(?:(?<hours>[0-9]+)H)?(?:(?<minutes>[0-9]+)M)?(?:(?<seconds>[0-9]+)S)?)?)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    private readonly CalendarEventSourceKind _sourceKind;
    private readonly string _providerAccountIdentitySha256;
    private readonly byte[] _documentBytes;
    private readonly DateTimeOffset _observedUtc;
    private readonly TimeZoneInfo _displayTimeZone;

    public LocalIcsCalendarProviderSnapshotSource(
        CalendarEventSourceKind sourceKind,
        string providerAccountIdentitySha256,
        ReadOnlySpan<byte> documentBytes,
        DateTimeOffset observedUtc,
        TimeZoneInfo? displayTimeZone = null)
    {
        if (sourceKind is not CalendarEventSourceKind.Google and
            not CalendarEventSourceKind.Apple)
            throw new ArgumentOutOfRangeException(nameof(sourceKind));
        ValidateLowercaseSha256(providerAccountIdentitySha256);
        if (documentBytes.IsEmpty || documentBytes.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The selected iCalendar file is empty or exceeds 1 MiB.");
        if (observedUtc.Offset != TimeSpan.Zero ||
            observedUtc < DateTimeOffset.UnixEpoch ||
            observedUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("The local iCalendar observation time is invalid.");

        _sourceKind = sourceKind;
        _providerAccountIdentitySha256 = providerAccountIdentitySha256;
        _documentBytes = documentBytes.ToArray();
        _observedUtc = observedUtc;
        _displayTimeZone = displayTimeZone ?? TimeZoneInfo.Local;
    }

    public Task<LocalCalendarProviderSnapshotBatch> ReadSnapshotBatchAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = ParseDocument(cancellationToken);
        return Task.FromResult(new LocalCalendarProviderSnapshotBatch(
            _sourceKind,
            _providerAccountIdentitySha256,
            document.Snapshots,
            IsLocalProtocolDouble: false,
            NetworkActionsInvoked: false,
            ProductionEndpointsInvoked: false,
            IsExplicitLocalFileImport: true,
            SeriesSnapshots: document.SeriesSnapshots));
    }

    private ParsedIcsDocument ParseDocument(
        CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(_documentBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The selected iCalendar file is not strict UTF-8.", exception);
        }
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        if (text.Contains('\0') ||
            text.Replace("\r\n", string.Empty, StringComparison.Ordinal)
                .Contains('\r'))
            throw new InvalidDataException("The selected iCalendar file has invalid line delimiters.");

        var physicalLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (physicalLines.Length > MaximumPhysicalLines)
            throw new InvalidDataException("The selected iCalendar file has too many physical lines.");
        var lines = Unfold(physicalLines);
        var components = new Stack<string>();
        var eventBuilders = new List<EventBuilder>();
        EventBuilder? currentEvent = null;
        var calendarSeen = false;
        var calendarEnded = false;
        var methodCancel = false;

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var property = ParseContentLine(line);
            if (property.Name == "BEGIN")
            {
                var component = ParseComponentName(property.Value);
                if (components.Count == 0)
                {
                    if (calendarSeen || component != "VCALENDAR")
                        throw new InvalidDataException("The iCalendar document has an invalid root component.");
                    calendarSeen = true;
                }
                else if (component == "VEVENT")
                {
                    if (components.Count != 1 || components.Peek() != "VCALENDAR" ||
                        currentEvent is not null)
                        throw new InvalidDataException("VEVENT must be a direct child of VCALENDAR.");
                    if (eventBuilders.Count >= LocalCalendarProviderImportCoordinator.MaximumSnapshotsPerBatch)
                        throw new InvalidDataException("The iCalendar file exceeds the 500-event limit.");
                    currentEvent = new EventBuilder();
                    currentEvent.CanonicalLines.Add(line);
                }
                else if (currentEvent is not null)
                {
                    currentEvent.CanonicalLines.Add(line);
                }
                components.Push(component);
                continue;
            }

            if (property.Name == "END")
            {
                var component = ParseComponentName(property.Value);
                if (components.Count == 0 || components.Peek() != component)
                    throw new InvalidDataException("The iCalendar component nesting is invalid.");
                if (currentEvent is not null) currentEvent.CanonicalLines.Add(line);
                if (component == "VEVENT" && components.Count == 2)
                {
                    eventBuilders.Add(currentEvent ??
                        throw new InvalidDataException("The iCalendar event boundary is invalid."));
                    currentEvent = null;
                }
                components.Pop();
                if (component == "VCALENDAR") calendarEnded = true;
                continue;
            }

            if (components.Count == 0 || calendarEnded)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    throw new InvalidDataException("The iCalendar document contains content outside VCALENDAR.");
                continue;
            }
            if (currentEvent is not null)
            {
                currentEvent.CanonicalLines.Add(line);
                if (components.Count == 2 && components.Peek() == "VEVENT")
                    currentEvent.Apply(property);
            }
            else if (components.Count == 1 && components.Peek() == "VCALENDAR" &&
                property.Name == "METHOD")
            {
                methodCancel = string.Equals(
                    property.Value.Trim(), "CANCEL", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!calendarSeen || !calendarEnded || components.Count != 0 || currentEvent is not null)
            throw new InvalidDataException("The iCalendar document is incomplete.");
        if (eventBuilders.Count == 0)
            throw new InvalidDataException("The iCalendar document contains no VEVENT components.");

        return BuildDocument(eventBuilders, methodCancel);
    }

    private ParsedIcsDocument BuildDocument(
        IReadOnlyList<EventBuilder> eventBuilders,
        bool methodCancel)
    {
        var snapshots = new List<CalendarProviderEventSnapshotImport>();
        var seriesSnapshots = new List<CalendarProviderSeriesSnapshotImport>();
        foreach (var group in eventBuilders.GroupBy(
                     builder => builder.ReadUid(), StringComparer.Ordinal))
        {
            var masters = group.Where(builder => !builder.HasRecurrenceId).ToArray();
            var exceptions = group.Where(builder => builder.HasRecurrenceId).ToArray();
            if (masters.Length != 1)
                throw new InvalidDataException(
                    "Each iCalendar UID requires exactly one master VEVENT in a local import.");
            var master = masters[0];
            if (!master.HasRecurrenceSet && exceptions.Length == 0)
            {
                snapshots.Add(master.BuildStandalone(
                    _sourceKind,
                    _providerAccountIdentitySha256,
                    _observedUtc,
                    _displayTimeZone,
                    methodCancel));
                continue;
            }

            var expanded = master.BuildSeries(
                exceptions,
                _sourceKind,
                _providerAccountIdentitySha256,
                _observedUtc,
                _displayTimeZone,
                methodCancel);
            snapshots.AddRange(expanded.Snapshots);
            seriesSnapshots.Add(expanded.SeriesSnapshot);
            if (snapshots.Count > LocalCalendarProviderImportCoordinator.MaximumSnapshotsPerBatch)
                throw new InvalidDataException(
                    "The expanded iCalendar file exceeds the 500-observation limit.");
        }
        return new ParsedIcsDocument(snapshots, seriesSnapshots);
    }

    private static List<string> Unfold(string[] physicalLines)
    {
        var lines = new List<string>(physicalLines.Length);
        foreach (var physicalLine in physicalLines)
        {
            if (physicalLine.Length == 0 && lines.Count == 0) continue;
            if (physicalLine.StartsWith(' ') || physicalLine.StartsWith('\t'))
            {
                if (lines.Count == 0)
                    throw new InvalidDataException("The iCalendar document starts with a folded line.");
                lines[^1] = string.Concat(lines[^1], physicalLine.AsSpan(1));
            }
            else
            {
                lines.Add(physicalLine);
            }
            if (StrictUtf8.GetByteCount(lines[^1]) > MaximumUnfoldedLineUtf8Bytes)
                throw new InvalidDataException("An unfolded iCalendar content line exceeds 32 KiB.");
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static ContentLine ParseContentLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            throw new InvalidDataException("The iCalendar document contains an empty content line.");
        var delimiter = FindUnquoted(line, ':');
        if (delimiter <= 0)
            throw new InvalidDataException("An iCalendar content line has no valid value delimiter.");
        var header = line[..delimiter];
        var parts = SplitUnquoted(header, ';');
        var name = parts[0].ToUpperInvariant();
        if (!IsToken(name))
            throw new InvalidDataException("An iCalendar property name is invalid.");
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parts.Skip(1))
        {
            var equals = parameter.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || equals == parameter.Length - 1)
                throw new InvalidDataException("An iCalendar property parameter is invalid.");
            var parameterName = parameter[..equals].ToUpperInvariant();
            var parameterValue = parameter[(equals + 1)..];
            if (!IsToken(parameterName) ||
                !parameters.TryAdd(parameterName, UnquoteParameter(parameterValue)))
                throw new InvalidDataException("An iCalendar property parameter is invalid or duplicated.");
        }
        return new ContentLine(name, parameters, line[(delimiter + 1)..]);
    }

    private static int FindUnquoted(string value, char delimiter)
    {
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '"') quoted = !quoted;
            else if (value[index] == delimiter && !quoted) return index;
        }
        if (quoted) throw new InvalidDataException("An iCalendar quoted parameter is incomplete.");
        return -1;
    }

    private static List<string> SplitUnquoted(string value, char delimiter)
    {
        var parts = new List<string>();
        var start = 0;
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '"') quoted = !quoted;
            else if (value[index] == delimiter && !quoted)
            {
                parts.Add(value[start..index]);
                start = index + 1;
            }
        }
        if (quoted) throw new InvalidDataException("An iCalendar quoted parameter is incomplete.");
        parts.Add(value[start..]);
        return parts;
    }

    private static string UnquoteParameter(string value)
    {
        if (!value.StartsWith('"')) return value;
        if (value.Length < 2 || !value.EndsWith('"'))
            throw new InvalidDataException("An iCalendar quoted parameter is incomplete.");
        return value[1..^1];
    }

    private static bool IsToken(string value) =>
        value.Length > 0 && value.All(character =>
            character is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-');

    private static string ParseComponentName(string value)
    {
        var name = value.Trim().ToUpperInvariant();
        if (!IsToken(name)) throw new InvalidDataException("An iCalendar component name is invalid.");
        return name;
    }

    private static void ValidateLowercaseSha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException(
                "Provider account identity must be a lowercase SHA-256 value.", nameof(value));
    }

    private static string HashIdentity(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class EventBuilder
    {
        private readonly Dictionary<string, ContentLine> _properties =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ContentLine> _recurrenceDates = [];
        private readonly List<ContentLine> _exceptionDates = [];

        public List<string> CanonicalLines { get; } = [];
        public bool HasRecurrenceId => _properties.ContainsKey("RECURRENCE-ID");
        public bool HasRecurrenceSet =>
            _properties.ContainsKey("RRULE") ||
            _recurrenceDates.Count != 0 ||
            _exceptionDates.Count != 0;

        public void Apply(ContentLine property)
        {
            if (property.Name == "RDATE")
            {
                _recurrenceDates.Add(property);
                return;
            }
            if (property.Name == "EXDATE")
            {
                _exceptionDates.Add(property);
                return;
            }
            if (property.Name is not (
                    "UID" or "DTSTART" or "DTEND" or "DURATION" or "SUMMARY" or
                    "DESCRIPTION" or "LOCATION" or "STATUS" or "SEQUENCE" or
                    "DTSTAMP" or "LAST-MODIFIED" or "RRULE" or "RECURRENCE-ID"))
                return;
            if (!_properties.TryAdd(property.Name, property))
                throw new InvalidDataException(
                    $"The iCalendar event contains duplicate {property.Name} properties.");
        }

        public string ReadUid() => RequiredText("UID", 1_024);

        public CalendarProviderEventSnapshotImport BuildStandalone(
            CalendarEventSourceKind sourceKind,
            string accountIdentity,
            DateTimeOffset observedUtc,
            TimeZoneInfo displayTimeZone,
            bool methodCancel)
        {
            if (HasRecurrenceId || HasRecurrenceSet)
                throw new InvalidOperationException(
                    "Recurring events must pass through the complete series expansion boundary.");
            var uid = ReadUid();
            var canonicalEvent = string.Join("\n", CanonicalLines);
            var eventIdentity = HashIdentity(string.Concat(
                sourceKind.ToString(), "\0", uid));
            var revisionIdentity = HashIdentity(string.Concat(
                sourceKind.ToString(), "\0", uid, "\0", canonicalEvent));
            if (IsCancelled(methodCancel))
            {
                return new CalendarProviderEventSnapshotImport(
                    sourceKind, accountIdentity, eventIdentity, revisionIdentity,
                    Title: string.Empty, StartLocal: default, EndLocal: default,
                    AllDay: false, Location: string.Empty, Notes: string.Empty,
                    IsDeleted: true, observedUtc);
            }
            var content = BuildActiveContent(displayTimeZone);
            return CreateSnapshot(
                sourceKind, accountIdentity, eventIdentity, revisionIdentity,
                seriesIdentity: null, content, observedUtc);
        }

        public ExpandedSeries BuildSeries(
            IReadOnlyList<EventBuilder> exceptions,
            CalendarEventSourceKind sourceKind,
            string accountIdentity,
            DateTimeOffset observedUtc,
            TimeZoneInfo displayTimeZone,
            bool methodCancel)
        {
            if (HasRecurrenceId)
                throw new InvalidDataException("A recurrence exception cannot be the master VEVENT.");
            var uid = ReadUid();
            var seriesIdentity = HashIdentity(string.Concat(
                sourceKind.ToString(), "\0", uid, "\0series"));
            var canonicalSeries = string.Join(
                "\n--NOVAEMAIL-RECURRENCE-COMPONENT--\n",
                new[] { this }.Concat(exceptions.OrderBy(
                        item => item.OptionalRaw("RECURRENCE-ID"), StringComparer.Ordinal))
                    .Select(item => string.Join("\n", item.CanonicalLines)));
            var seriesRevisionIdentity = HashIdentity(string.Concat(
                sourceKind.ToString(), "\0", uid, "\0", canonicalSeries));
            if (IsCancelled(methodCancel))
            {
                return new ExpandedSeries(
                    [],
                    new CalendarProviderSeriesSnapshotImport(
                        sourceKind, accountIdentity, seriesIdentity,
                        seriesRevisionIdentity, [], observedUtc));
            }

            var masterContent = BuildActiveContent(displayTimeZone);
            var recurrenceCoordinates = new HashSet<DateTime> { masterContent.Start.Coordinate };
            if (_properties.TryGetValue("RRULE", out var recurrenceRule))
            {
                if (recurrenceRule.Parameters.Count != 0)
                    throw new InvalidDataException(
                        "RRULE parameters are not supported by the bounded local importer.");
                foreach (var coordinate in BoundedIcsRecurrenceExpander.Expand(
                             masterContent.Start.Coordinate,
                             recurrenceRule.Value,
                             value => ParseUntilCoordinate(
                                 value, masterContent.Start, displayTimeZone)))
                    recurrenceCoordinates.Add(coordinate);
            }
            foreach (var value in ParseRecurrenceValues(
                         _recurrenceDates, masterContent.Start, displayTimeZone, "RDATE"))
                recurrenceCoordinates.Add(value.Coordinate);
            var generatedCoordinates = recurrenceCoordinates.ToHashSet();
            var excludedCoordinates = ParseRecurrenceValues(
                    _exceptionDates, masterContent.Start, displayTimeZone, "EXDATE")
                .Select(value => value.Coordinate)
                .ToHashSet();
            recurrenceCoordinates.ExceptWith(excludedCoordinates);

            var exceptionByCoordinate = new Dictionary<DateTime, EventBuilder>();
            foreach (var exception in exceptions)
            {
                if (exception.HasRecurrenceSet)
                    throw new InvalidDataException(
                        "A recurrence exception cannot define RRULE, RDATE, or EXDATE.");
                if (!exception._properties.TryGetValue("RECURRENCE-ID", out var recurrenceId))
                    throw new InvalidDataException("A recurring exception requires RECURRENCE-ID.");
                EnsureOnlyParameters(
                    recurrenceId, "RECURRENCE-ID", "VALUE", "TZID", "RANGE");
                if (recurrenceId.Parameters.ContainsKey("RANGE"))
                    throw new InvalidDataException(
                        "RECURRENCE-ID RANGE is not supported by the bounded occurrence importer.");
                var recurrenceValue = ParseCalendarValue(recurrenceId, displayTimeZone);
                RequireSameRecurrenceDomain(
                    masterContent.Start, recurrenceValue, "RECURRENCE-ID");
                if (!generatedCoordinates.Contains(recurrenceValue.Coordinate) ||
                    !exceptionByCoordinate.TryAdd(recurrenceValue.Coordinate, exception))
                {
                    throw new InvalidDataException(
                        "A recurrence exception is duplicated or does not identify a generated occurrence.");
                }
                if (exception.IsCancelled(methodCancel))
                    recurrenceCoordinates.Remove(recurrenceValue.Coordinate);
                else if (!excludedCoordinates.Contains(recurrenceValue.Coordinate))
                    recurrenceCoordinates.Add(recurrenceValue.Coordinate);
            }

            var snapshots = new List<CalendarProviderEventSnapshotImport>();
            foreach (var coordinate in recurrenceCoordinates.Order())
            {
                var occurrenceIdentityToken = FormatRecurrenceIdentity(
                    masterContent.Start, coordinate);
                var eventIdentity = HashIdentity(string.Concat(
                    sourceKind.ToString(), "\0", uid, "\0occurrence\0",
                    occurrenceIdentityToken));
                EventContent content;
                string canonicalOccurrence;
                if (exceptionByCoordinate.TryGetValue(coordinate, out var exception))
                {
                    content = exception.BuildActiveContent(displayTimeZone);
                    RequireSameRecurrenceDomain(
                        masterContent.Start, content.Start, "recurrence exception DTSTART");
                    canonicalOccurrence = string.Join("\n", exception.CanonicalLines);
                }
                else
                {
                    content = RelocateContent(masterContent, coordinate, displayTimeZone);
                    canonicalOccurrence = string.Join("\n", CanonicalLines);
                }
                var revisionIdentity = HashIdentity(string.Concat(
                    seriesRevisionIdentity, "\0", eventIdentity, "\0", canonicalOccurrence));
                snapshots.Add(CreateSnapshot(
                    sourceKind, accountIdentity, eventIdentity, revisionIdentity,
                    seriesIdentity, content, observedUtc));
            }
            if (snapshots.Count > BoundedIcsRecurrenceExpander.MaximumOccurrences)
                throw new InvalidDataException(
                    "The complete recurrence set exceeds the 500-occurrence limit.");
            return new ExpandedSeries(
                snapshots,
                new CalendarProviderSeriesSnapshotImport(
                    sourceKind,
                    accountIdentity,
                    seriesIdentity,
                    seriesRevisionIdentity,
                    snapshots.Select(item => item.ProviderEventIdentitySha256).ToArray(),
                    observedUtc));
        }

        private EventContent BuildActiveContent(TimeZoneInfo displayTimeZone)
        {
            if (!_properties.TryGetValue("DTSTART", out var startProperty))
                throw new InvalidDataException("A non-cancelled iCalendar event requires DTSTART.");
            var start = ParseCalendarValue(startProperty, displayTimeZone);
            ParsedCalendarValue? end = null;
            if (_properties.TryGetValue("DTEND", out var endProperty))
                end = ParseCalendarValue(endProperty, displayTimeZone);
            if (_properties.ContainsKey("DTEND") && _properties.ContainsKey("DURATION"))
                throw new InvalidDataException("An iCalendar event cannot contain both DTEND and DURATION.");
            if (end is not null)
                RequireSameRecurrenceDomain(start, end.Value, "DTEND");

            TimeSpan duration;
            var exactDuration = false;
            DateTime endLocal;
            if (end is not null)
            {
                duration = start.InstantUtc is not null && end.Value.InstantUtc is not null
                    ? end.Value.InstantUtc.Value - start.InstantUtc.Value
                    : end.Value.Coordinate - start.Coordinate;
                exactDuration = start.InstantUtc is not null && end.Value.InstantUtc is not null;
                endLocal = end.Value.Local;
            }
            else if (_properties.TryGetValue("DURATION", out var durationProperty))
            {
                duration = ParsePositiveDuration(durationProperty.Value, start.AllDay);
                endLocal = ProjectCalendarCoordinate(
                    start, start.Coordinate.Add(duration), displayTimeZone).Local;
            }
            else if (start.AllDay)
            {
                duration = TimeSpan.FromDays(1);
                endLocal = start.Local.AddDays(1);
            }
            else
            {
                throw new InvalidDataException(
                    "A zero-duration timed iCalendar event cannot be represented safely without DTEND or DURATION.");
            }
            if (duration <= TimeSpan.Zero || endLocal <= start.Local)
                throw new InvalidDataException("The iCalendar event end must follow its start.");

            return new EventContent(
                OptionalText("SUMMARY", 160, "Untitled calendar event"),
                start,
                endLocal,
                duration,
                exactDuration,
                OptionalText("LOCATION", 512, string.Empty),
                OptionalText("DESCRIPTION", 16_384, string.Empty));
        }

        private static EventContent RelocateContent(
            EventContent content,
            DateTime coordinate,
            TimeZoneInfo displayTimeZone)
        {
            var start = ProjectCalendarCoordinate(content.Start, coordinate, displayTimeZone);
            DateTime endLocal;
            if (content.ExactDuration && start.InstantUtc is not null)
            {
                var end = TimeZoneInfo.ConvertTime(
                    start.InstantUtc.Value.Add(content.Duration), displayTimeZone);
                endLocal = DateTime.SpecifyKind(end.DateTime, DateTimeKind.Unspecified);
            }
            else
            {
                endLocal = ProjectCalendarCoordinate(
                    content.Start, coordinate.Add(content.Duration), displayTimeZone).Local;
            }
            if (endLocal <= start.Local)
                throw new InvalidDataException(
                    "A recurring occurrence produced a non-increasing display range.");
            return content with { Start = start, EndLocal = endLocal };
        }

        private static CalendarProviderEventSnapshotImport CreateSnapshot(
            CalendarEventSourceKind sourceKind,
            string accountIdentity,
            string eventIdentity,
            string revisionIdentity,
            string? seriesIdentity,
            EventContent content,
            DateTimeOffset observedUtc) =>
            new(
                sourceKind,
                accountIdentity,
                eventIdentity,
                revisionIdentity,
                content.Title,
                content.Start.Local,
                content.EndLocal,
                content.Start.AllDay,
                content.Location,
                content.Notes,
                IsDeleted: false,
                observedUtc,
                seriesIdentity);

        private bool IsCancelled(bool methodCancel) => methodCancel || string.Equals(
            OptionalRaw("STATUS"), "CANCELLED", StringComparison.OrdinalIgnoreCase);

        private static List<ParsedCalendarValue> ParseRecurrenceValues(
            IReadOnlyList<ContentLine> properties,
            ParsedCalendarValue masterStart,
            TimeZoneInfo displayTimeZone,
            string propertyName)
        {
            var values = new List<ParsedCalendarValue>();
            foreach (var property in properties)
            {
                EnsureOnlyParameters(property, propertyName, "VALUE", "TZID");
                if (property.Parameters.TryGetValue("VALUE", out var valueType) &&
                    string.Equals(valueType, "PERIOD", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"{propertyName} PERIOD values are not supported by the occurrence importer.");
                foreach (var rawValue in property.Value.Split(','))
                {
                    if (values.Count == BoundedIcsRecurrenceExpander.MaximumOccurrences)
                        throw new InvalidDataException(
                            $"{propertyName} exceeds the 500-value limit.");
                    var parsed = ParseCalendarValue(
                        property with { Value = rawValue }, displayTimeZone);
                    RequireSameRecurrenceDomain(masterStart, parsed, propertyName);
                    values.Add(parsed);
                }
            }
            return values;
        }

        private static DateTime ParseUntilCoordinate(
            string value,
            ParsedCalendarValue masterStart,
            TimeZoneInfo displayTimeZone)
        {
            ContentLine property;
            switch (masterStart.Domain)
            {
                case CalendarValueDomain.Date:
                    property = new ContentLine(
                        "UNTIL", new Dictionary<string, string> { ["VALUE"] = "DATE" }, value);
                    break;
                case CalendarValueDomain.Floating:
                    if (value.EndsWith('Z'))
                        throw new InvalidDataException(
                            "A floating DTSTART requires a floating UNTIL value.");
                    property = new ContentLine("UNTIL", new Dictionary<string, string>(), value);
                    break;
                case CalendarValueDomain.Utc:
                    if (!value.EndsWith('Z'))
                        throw new InvalidDataException("A UTC DTSTART requires a UTC UNTIL value.");
                    property = new ContentLine("UNTIL", new Dictionary<string, string>(), value);
                    break;
                case CalendarValueDomain.Zoned:
                    if (!value.EndsWith('Z'))
                        throw new InvalidDataException(
                            "A TZID DTSTART requires the RFC 5545 UNTIL value in UTC.");
                    var utc = ParseCalendarValue(
                        new ContentLine("UNTIL", new Dictionary<string, string>(), value),
                        displayTimeZone);
                    var source = TimeZoneInfo.ConvertTime(
                        utc.InstantUtc!.Value, masterStart.SourceTimeZone!);
                    return DateTime.SpecifyKind(source.DateTime, DateTimeKind.Unspecified);
                default:
                    throw new InvalidDataException("The recurrence time domain is unsupported.");
            }
            var parsed = ParseCalendarValue(property, displayTimeZone);
            RequireSameRecurrenceDomain(masterStart, parsed, "UNTIL");
            return parsed.Coordinate;
        }

        private static void EnsureOnlyParameters(
            ContentLine property,
            string propertyName,
            params string[] permittedNames)
        {
            var permitted = permittedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unsupported = property.Parameters.Keys
                .Where(name => !permitted.Contains(name))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
            if (unsupported is not null)
                throw new InvalidDataException(
                    $"{propertyName} parameter {unsupported} is not supported by the bounded occurrence importer.");
        }

        private static string FormatRecurrenceIdentity(
            ParsedCalendarValue masterStart,
            DateTime coordinate) => string.Concat(
                masterStart.Domain.ToString(), "\0",
                masterStart.SourceTimeZoneId ?? string.Empty, "\0",
                coordinate.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));

        private string RequiredText(string name, int maximum)
        {
            var result = OptionalText(name, maximum, string.Empty);
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidDataException($"The iCalendar event requires {name}.");
            return result;
        }

        private string OptionalText(string name, int maximum, string fallback)
        {
            var value = OptionalRaw(name);
            if (value is null) return fallback;
            var decoded = UnescapeText(value).Normalize(NormalizationForm.FormC);
            if (decoded.Length > maximum || decoded.Any(character =>
                    char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
                throw new InvalidDataException($"The iCalendar {name} value exceeds its safety limit.");
            return string.IsNullOrWhiteSpace(decoded) ? fallback : decoded;
        }

        private string? OptionalRaw(string name) =>
            _properties.TryGetValue(name, out var property) ? property.Value.Trim() : null;
    }

    private static ParsedCalendarValue ParseCalendarValue(
        ContentLine property,
        TimeZoneInfo displayTimeZone)
    {
        property.Parameters.TryGetValue("VALUE", out var valueType);
        property.Parameters.TryGetValue("TZID", out var timeZoneId);
        var isDate = string.Equals(valueType, "DATE", StringComparison.OrdinalIgnoreCase) ||
            (valueType is null && property.Value.Length == 8);
        if (isDate)
        {
            if (!string.IsNullOrEmpty(timeZoneId) ||
                !DateTime.TryParseExact(
                    property.Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                throw new InvalidDataException("An all-day iCalendar date is invalid.");
            var coordinate = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            return new ParsedCalendarValue(
                coordinate,
                AllDay: true,
                coordinate,
                CalendarValueDomain.Date,
                SourceTimeZoneId: null,
                SourceTimeZone: null,
                InstantUtc: null);
        }
        if (valueType is not null &&
            !string.Equals(valueType, "DATE-TIME", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The iCalendar date value type is unsupported.");

        var utc = property.Value.EndsWith('Z');
        var value = utc ? property.Value[..^1] : property.Value;
        if (!DateTime.TryParseExact(
                value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            throw new InvalidDataException("An iCalendar date-time is invalid.");
        parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (utc)
        {
            if (!string.IsNullOrEmpty(timeZoneId))
                throw new InvalidDataException("A UTC iCalendar value cannot also specify TZID.");
            var converted = TimeZoneInfo.ConvertTime(
                new DateTimeOffset(parsed, TimeSpan.Zero), displayTimeZone);
            return new ParsedCalendarValue(
                DateTime.SpecifyKind(converted.DateTime, DateTimeKind.Unspecified),
                AllDay: false,
                parsed,
                CalendarValueDomain.Utc,
                SourceTimeZoneId: null,
                SourceTimeZone: null,
                new DateTimeOffset(parsed, TimeSpan.Zero));
        }
        if (string.IsNullOrEmpty(timeZoneId))
            return new ParsedCalendarValue(
                parsed,
                AllDay: false,
                parsed,
                CalendarValueDomain.Floating,
                SourceTimeZoneId: null,
                SourceTimeZone: null,
                InstantUtc: null);

        var sourceZone = ResolveTimeZone(timeZoneId);
        if (sourceZone.IsInvalidTime(parsed) || sourceZone.IsAmbiguousTime(parsed))
            throw new InvalidDataException(
                "An iCalendar local time is invalid or ambiguous in its declared time zone.");
        var sourceOffset = new DateTimeOffset(parsed, sourceZone.GetUtcOffset(parsed));
        var local = TimeZoneInfo.ConvertTime(sourceOffset, displayTimeZone);
        return new ParsedCalendarValue(
            DateTime.SpecifyKind(local.DateTime, DateTimeKind.Unspecified),
            AllDay: false,
            parsed,
            CalendarValueDomain.Zoned,
            CanonicalizeTimeZoneId(timeZoneId, sourceZone),
            sourceZone,
            sourceOffset);
    }

    private static string CanonicalizeTimeZoneId(
        string suppliedTimeZoneId,
        TimeZoneInfo sourceZone)
    {
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(
                suppliedTimeZoneId, out var windowsId) &&
            !string.IsNullOrWhiteSpace(windowsId))
            return windowsId;
        return sourceZone.Id;
    }

    private static void RequireSameRecurrenceDomain(
        ParsedCalendarValue expected,
        ParsedCalendarValue actual,
        string propertyName)
    {
        if (expected.AllDay != actual.AllDay || expected.Domain != actual.Domain ||
            expected.Domain == CalendarValueDomain.Zoned &&
            !string.Equals(
                expected.SourceTimeZoneId,
                actual.SourceTimeZoneId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{propertyName} must use the same DATE, floating, UTC, or TZID domain as DTSTART.");
        }
    }

    private static ParsedCalendarValue ProjectCalendarCoordinate(
        ParsedCalendarValue template,
        DateTime coordinate,
        TimeZoneInfo displayTimeZone)
    {
        if (coordinate.Kind != DateTimeKind.Unspecified)
            throw new InvalidDataException("A recurrence coordinate must have unspecified DateTime kind.");
        return template.Domain switch
        {
            CalendarValueDomain.Date => template with
            {
                Local = coordinate,
                Coordinate = coordinate,
                InstantUtc = null,
            },
            CalendarValueDomain.Floating => template with
            {
                Local = coordinate,
                Coordinate = coordinate,
                InstantUtc = null,
            },
            CalendarValueDomain.Utc => ProjectUtcCoordinate(template, coordinate, displayTimeZone),
            CalendarValueDomain.Zoned => ProjectZonedCoordinate(template, coordinate, displayTimeZone),
            _ => throw new InvalidDataException("The recurrence time domain is unsupported."),
        };
    }

    private static ParsedCalendarValue ProjectUtcCoordinate(
        ParsedCalendarValue template,
        DateTime coordinate,
        TimeZoneInfo displayTimeZone)
    {
        var instant = new DateTimeOffset(coordinate, TimeSpan.Zero);
        var display = TimeZoneInfo.ConvertTime(instant, displayTimeZone);
        return template with
        {
            Local = DateTime.SpecifyKind(display.DateTime, DateTimeKind.Unspecified),
            Coordinate = coordinate,
            InstantUtc = instant,
        };
    }

    private static ParsedCalendarValue ProjectZonedCoordinate(
        ParsedCalendarValue template,
        DateTime coordinate,
        TimeZoneInfo displayTimeZone)
    {
        var sourceZone = template.SourceTimeZone ??
            throw new InvalidDataException("A TZID recurrence lost its source time-zone context.");
        if (sourceZone.IsInvalidTime(coordinate) || sourceZone.IsAmbiguousTime(coordinate))
            throw new InvalidDataException(
                "A recurring occurrence is invalid or ambiguous in its declared time zone.");
        var instant = new DateTimeOffset(coordinate, sourceZone.GetUtcOffset(coordinate));
        var display = TimeZoneInfo.ConvertTime(instant, displayTimeZone);
        return template with
        {
            Local = DateTime.SpecifyKind(display.DateTime, DateTimeKind.Unspecified),
            Coordinate = coordinate,
            InstantUtc = instant,
        };
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (timeZoneId.Length > 256 || timeZoneId.Any(char.IsControl))
            throw new InvalidDataException("The iCalendar TZID is invalid.");
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId) &&
                !string.IsNullOrWhiteSpace(windowsId))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
                catch (TimeZoneNotFoundException) { }
            }
        }
        catch (InvalidTimeZoneException) { }
        throw new InvalidDataException("The iCalendar TZID is unavailable or invalid on this system.");
    }

    private static TimeSpan ParsePositiveDuration(string value, bool allDay)
    {
        var match = DurationPattern.Match(value);
        if (!match.Success || !match.Groups.Cast<Group>().Skip(1).Any(group => group.Success))
            throw new InvalidDataException("The iCalendar duration is invalid.");
        try
        {
            var weeks = ParseDurationPart(match, "weeks");
            var days = ParseDurationPart(match, "days");
            var hours = ParseDurationPart(match, "hours");
            var minutes = ParseDurationPart(match, "minutes");
            var seconds = ParseDurationPart(match, "seconds");
            var duration = TimeSpan.FromDays(checked(weeks * 7L + days)) +
                TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) +
                TimeSpan.FromSeconds(seconds);
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(366) ||
                (allDay && duration.TotalDays % 1 != 0))
                throw new InvalidDataException("The iCalendar duration is outside its supported range.");
            return duration;
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The iCalendar duration is outside its supported range.", exception);
        }
    }

    private static long ParseDurationPart(Match match, string name) =>
        match.Groups[name].Success
            ? long.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture)
            : 0;

    private static string UnescapeText(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\')
            {
                result.Append(value[index]);
                continue;
            }
            if (++index >= value.Length)
                throw new InvalidDataException("An iCalendar text escape is incomplete.");
            result.Append(value[index] switch
            {
                '\\' => '\\',
                ',' => ',',
                ';' => ';',
                'n' or 'N' => '\n',
                _ => throw new InvalidDataException("An iCalendar text escape is unsupported."),
            });
        }
        return result.ToString();
    }

    private sealed record ContentLine(
        string Name,
        IReadOnlyDictionary<string, string> Parameters,
        string Value);

    private sealed record ParsedIcsDocument(
        IReadOnlyList<CalendarProviderEventSnapshotImport> Snapshots,
        IReadOnlyList<CalendarProviderSeriesSnapshotImport> SeriesSnapshots);

    private sealed record ExpandedSeries(
        IReadOnlyList<CalendarProviderEventSnapshotImport> Snapshots,
        CalendarProviderSeriesSnapshotImport SeriesSnapshot);

    private sealed record EventContent(
        string Title,
        ParsedCalendarValue Start,
        DateTime EndLocal,
        TimeSpan Duration,
        bool ExactDuration,
        string Location,
        string Notes);

    private enum CalendarValueDomain
    {
        Date,
        Floating,
        Utc,
        Zoned,
    }

    private readonly record struct ParsedCalendarValue(
        DateTime Local,
        bool AllDay,
        DateTime Coordinate,
        CalendarValueDomain Domain,
        string? SourceTimeZoneId,
        TimeZoneInfo? SourceTimeZone,
        DateTimeOffset? InstantUtc);
}
