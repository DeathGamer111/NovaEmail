using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaEmail.Intelligence;

public sealed record CalendarScanInput(
    string MessageIdentity,
    string Subject,
    string PlainText,
    DateTimeOffset ReferenceTime);

public sealed record CalendarEvidenceSuggestion(
    string EvidenceId,
    string EvidenceText,
    int CharacterOffset,
    DateTime StartLocal,
    bool AllDay,
    string SuggestedTitle,
    bool RequiresUserConfirmation,
    bool AiEnriched);

public sealed record CalendarEvidenceScanResult(
    IReadOnlyList<CalendarEvidenceSuggestion> Suggestions,
    bool InputTruncated,
    int ScannedCharacters);

/// <summary>
/// Finds conservative, evidence-bound date candidates without network access.
/// Results are suggestions only and can never mutate a calendar.
/// </summary>
public static partial class CalendarEvidenceScanner
{
    public const int MaximumInputCharacters = 65_536;
    public const int MaximumSuggestions = 16;

    private static readonly Dictionary<string, int> Months =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["jan"] = 1, ["january"] = 1,
            ["feb"] = 2, ["february"] = 2,
            ["mar"] = 3, ["march"] = 3,
            ["apr"] = 4, ["april"] = 4,
            ["may"] = 5,
            ["jun"] = 6, ["june"] = 6,
            ["jul"] = 7, ["july"] = 7,
            ["aug"] = 8, ["august"] = 8,
            ["sep"] = 9, ["sept"] = 9, ["september"] = 9,
            ["oct"] = 10, ["october"] = 10,
            ["nov"] = 11, ["november"] = 11,
            ["dec"] = 12, ["december"] = 12,
        };

    public static CalendarEvidenceScanResult Scan(CalendarScanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.MessageIdentity);
        if (input.MessageIdentity.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(input), "Message identity exceeds its safety limit.");

        var normalized = Normalize(input.PlainText ?? string.Empty);
        var truncated = normalized.Length > MaximumInputCharacters;
        if (truncated) normalized = normalized[..MaximumInputCharacters];
        var title = NormalizeTitle(input.Subject);
        var candidates = new List<CalendarEvidenceSuggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        AddMatches(IsoDatePattern(), normalized, input, title, candidates, seen, ParseNumeric);
        AddMatches(MonthNamePattern(), normalized, input, title, candidates, seen, ParseNamedMonth);
        AddMatches(UsNumericPattern(), normalized, input, title, candidates, seen, ParseNumeric);

        return new CalendarEvidenceScanResult(
            candidates
                .OrderBy(candidate => candidate.StartLocal)
                .ThenBy(candidate => candidate.CharacterOffset)
                .Take(MaximumSuggestions)
                .ToArray(),
            truncated,
            normalized.Length);
    }

    private static void AddMatches(
        Regex pattern,
        string text,
        CalendarScanInput input,
        string title,
        List<CalendarEvidenceSuggestion> candidates,
        HashSet<string> seen,
        Func<Match, DateTimeOffset, DateTime?> parser)
    {
        foreach (Match match in pattern.Matches(text))
        {
            if (candidates.Count >= MaximumSuggestions) return;
            var parsed = parser(match, input.ReferenceTime);
            if (parsed is null) continue;
            var evidence = match.Value.Trim();
            var key = string.Create(
                CultureInfo.InvariantCulture,
                $"{match.Index}:{evidence}:{parsed.Value:O}");
            if (!seen.Add(key)) continue;
            candidates.Add(new CalendarEvidenceSuggestion(
                EvidenceId(input.MessageIdentity, match.Index, evidence),
                evidence,
                match.Index,
                parsed.Value,
                !match.Groups["hour"].Success,
                title,
                RequiresUserConfirmation: true,
                AiEnriched: false));
        }
    }

    private static DateTime? ParseNamedMonth(Match match, DateTimeOffset reference)
    {
        var monthName = match.Groups["monthName"].Value.Replace(".", string.Empty, StringComparison.Ordinal);
        if (!Months.TryGetValue(monthName, out var month)) return null;
        return ParseParts(match, reference, month);
    }

    private static DateTime? ParseNumeric(Match match, DateTimeOffset reference)
    {
        if (!int.TryParse(match.Groups["month"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var month)) return null;
        return ParseParts(match, reference, month);
    }

    private static DateTime? ParseParts(Match match, DateTimeOffset reference, int month)
    {
        if (!int.TryParse(match.Groups["day"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var day)) return null;
        var year = ResolveYear(match.Groups["year"].Value, reference.Year);
        if (year is null) return null;
        var hour = 0;
        var minute = 0;
        if (match.Groups["hour"].Success)
        {
            if (!int.TryParse(match.Groups["hour"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out hour) ||
                (match.Groups["minute"].Success &&
                 !int.TryParse(match.Groups["minute"].Value, NumberStyles.None,
                     CultureInfo.InvariantCulture, out minute)) ||
                minute is < 0 or > 59)
                return null;
            var ampm = match.Groups["ampm"].Value.Replace(".", string.Empty, StringComparison.Ordinal)
                .ToUpperInvariant();
            if (ampm.Length > 0)
            {
                if (hour is < 1 or > 12 || ampm is not ("AM" or "PM")) return null;
                if (hour == 12) hour = 0;
                if (ampm == "PM") hour += 12;
            }
            else if (hour is < 0 or > 23)
            {
                return null;
            }
        }

        DateTime candidate;
        try
        {
            candidate = new DateTime(year.Value, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        if (!match.Groups["year"].Success &&
            candidate.Date < reference.LocalDateTime.Date.AddDays(-30))
        {
            try { candidate = candidate.AddYears(1); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return candidate;
    }

    private static int? ResolveYear(string text, int fallback)
    {
        if (string.IsNullOrEmpty(text)) return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var year)) return null;
        if (text.Length == 2) year += year >= 70 ? 1900 : 2000;
        return year is >= 1970 and <= 2100 ? year : null;
    }

    private static string Normalize(string value)
    {
        var source = value.Length > MaximumInputCharacters + 1
            ? value[..(MaximumInputCharacters + 1)]
            : value;
        var builder = new StringBuilder(Math.Min(source.Length, MaximumInputCharacters + 1));
        foreach (var character in source.Normalize(NormalizationForm.FormC))
        {
            if (character is '\r' or '\n' or '\t' || !char.IsControl(character)) builder.Append(character);
            if (builder.Length > MaximumInputCharacters) break;
        }
        return builder.ToString();
    }

    private static string NormalizeTitle(string subject)
    {
        var normalized = Normalize(subject ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length > 160) normalized = normalized[..160].TrimEnd();
        return string.IsNullOrWhiteSpace(normalized) ? "Event from email" : normalized;
    }

    private static string EvidenceId(string messageIdentity, int offset, string evidence)
    {
        var material = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{messageIdentity}\n{offset}\n{evidence}"));
        return Convert.ToHexStringLower(SHA256.HashData(material));
    }

    [GeneratedRegex(
        @"\b(?<year>20\d{2})-(?<month>0?[1-9]|1[0-2])-(?<day>0?[1-9]|[12]\d|3[01])(?:[ T](?<hour>[01]?\d|2[0-3]):(?<minute>[0-5]\d)(?:\s*(?<ampm>a\.?m\.?|p\.?m\.?))?)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex IsoDatePattern();

    [GeneratedRegex(
        @"\b(?<monthName>Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\.?\s+(?<day>0?[1-9]|[12]\d|3[01])(?:st|nd|rd|th)?(?:,?\s+(?<year>20\d{2}))?(?:\s+(?:at\s+)?(?<hour>0?[1-9]|1[0-2])(?::(?<minute>[0-5]\d))?\s*(?<ampm>a\.?m\.?|p\.?m\.?))?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex MonthNamePattern();

    [GeneratedRegex(
        @"\b(?<month>0?[1-9]|1[0-2])/(?<day>0?[1-9]|[12]\d|3[01])(?:/(?<year>\d{2}|20\d{2}))?(?:\s+(?<hour>0?\d|1\d|2[0-3]):(?<minute>[0-5]\d)(?:\s*(?<ampm>a\.?m\.?|p\.?m\.?))?)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex UsNumericPattern();
}
