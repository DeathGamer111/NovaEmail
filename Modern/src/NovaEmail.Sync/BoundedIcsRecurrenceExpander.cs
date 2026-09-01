using System.Globalization;

namespace NovaEmail.Sync;

/// <summary>
/// Expands the date-level RFC 5545 recurrence shapes that NovaEmail can fully
/// enumerate into its append-only series snapshot. Unbounded and unsupported
/// rules fail closed rather than being truncated and mislabeled as complete.
/// </summary>
internal static class BoundedIcsRecurrenceExpander
{
    public const int MaximumOccurrences = 500;
    public const int MaximumCandidateDays = 100_000;

    public static IReadOnlyList<DateTime> Expand(
        DateTime startCoordinate,
        string ruleText,
        Func<string, DateTime> parseUntilCoordinate)
    {
        ArgumentNullException.ThrowIfNull(ruleText);
        ArgumentNullException.ThrowIfNull(parseUntilCoordinate);
        if (startCoordinate.Kind != DateTimeKind.Unspecified)
            throw new InvalidDataException("A recurrence start must be a floating coordinate.");
        var rule = Parse(ruleText, parseUntilCoordinate);
        if (!Matches(startCoordinate, startCoordinate, rule))
            throw new InvalidDataException(
                "DTSTART is not synchronized with its recurrence rule.");

        var results = new List<DateTime>();
        var candidate = startCoordinate;
        var reachedNaturalBoundary = false;
        for (var inspected = 0; inspected < MaximumCandidateDays; inspected++)
        {
            if (rule.UntilCoordinate is not null && candidate > rule.UntilCoordinate.Value)
            {
                reachedNaturalBoundary = true;
                break;
            }
            if (Matches(candidate, startCoordinate, rule))
            {
                results.Add(candidate);
                if (rule.Count is not null && results.Count == rule.Count.Value) return results;
                if (results.Count > MaximumOccurrences)
                    throw new InvalidDataException(
                        "The recurrence rule exceeds the 500-occurrence limit.");
            }
            if (rule.UntilCoordinate is not null &&
                candidate.Date >= rule.UntilCoordinate.Value.Date)
            {
                reachedNaturalBoundary = true;
                break;
            }
            if (candidate.Date == DateTime.MaxValue.Date)
            {
                reachedNaturalBoundary = true;
                break;
            }
            candidate = candidate.AddDays(1);
        }

        if (rule.Count is not null && results.Count != rule.Count.Value)
            throw new InvalidDataException(
                "The recurrence rule could not be completely enumerated inside the supported date range.");
        if (rule.UntilCoordinate is not null && !reachedNaturalBoundary)
        {
            throw new InvalidDataException(
                "The recurrence rule exceeds the 100,000-candidate safety limit.");
        }
        return results;
    }

    private static RecurrenceRule Parse(
        string ruleText,
        Func<string, DateTime> parseUntilCoordinate)
    {
        if (string.IsNullOrWhiteSpace(ruleText) || ruleText.Length > 4_096 ||
            ruleText.Any(char.IsControl))
            throw new InvalidDataException("The recurrence rule is empty or exceeds its safety limit.");
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in ruleText.Split(';'))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || equals == part.Length - 1)
                throw new InvalidDataException("The recurrence rule contains an invalid part.");
            var name = part[..equals].ToUpperInvariant();
            var value = part[(equals + 1)..];
            if (!name.All(character => character is >= 'A' and <= 'Z') ||
                !parts.TryAdd(name, value))
                throw new InvalidDataException(
                    "The recurrence rule contains an invalid or duplicate part.");
        }
        foreach (var name in parts.Keys)
        {
            if (name is not (
                    "FREQ" or "COUNT" or "UNTIL" or "INTERVAL" or "BYDAY" or
                    "BYMONTHDAY" or "BYMONTH" or "WKST"))
            {
                throw new InvalidDataException(
                    $"The recurrence rule part {name} is not supported by the bounded date-level expander.");
            }
        }
        if (!parts.TryGetValue("FREQ", out var frequencyText) ||
            !Enum.TryParse<RecurrenceFrequency>(
                frequencyText, ignoreCase: true, out var frequency) ||
            !Enum.IsDefined(frequency))
            throw new InvalidDataException(
                "The recurrence rule requires DAILY, WEEKLY, MONTHLY, or YEARLY FREQ.");
        var hasCount = parts.TryGetValue("COUNT", out var countText);
        var hasUntil = parts.TryGetValue("UNTIL", out var untilText);
        if (hasCount == hasUntil)
            throw new InvalidDataException(
                "A bounded recurrence rule requires exactly one COUNT or UNTIL value.");
        int? count = null;
        DateTime? until = null;
        if (hasCount)
        {
            if (!int.TryParse(
                    countText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCount) ||
                parsedCount is < 1 or > MaximumOccurrences)
                throw new InvalidDataException("COUNT must be between 1 and 500.");
            count = parsedCount;
        }
        else
        {
            until = parseUntilCoordinate(untilText!);
            if (until.Value.Kind != DateTimeKind.Unspecified)
                throw new InvalidDataException("UNTIL did not resolve to a floating recurrence coordinate.");
        }

        var interval = 1;
        if (parts.TryGetValue("INTERVAL", out var intervalText) &&
            (!int.TryParse(
                 intervalText, NumberStyles.None, CultureInfo.InvariantCulture, out interval) ||
             interval is < 1 or > 366))
            throw new InvalidDataException("INTERVAL must be between 1 and 366.");
        var byMonths = parts.TryGetValue("BYMONTH", out var byMonthText)
            ? ParseIntegerList(byMonthText, 1, 12, allowNegative: false, "BYMONTH")
            : null;
        var byMonthDays = parts.TryGetValue("BYMONTHDAY", out var byMonthDayText)
            ? ParseIntegerList(byMonthDayText, 1, 31, allowNegative: true, "BYMONTHDAY")
            : null;
        var byDays = parts.TryGetValue("BYDAY", out var byDayText)
            ? ParseWeekdays(byDayText)
            : null;
        var weekStart = parts.TryGetValue("WKST", out var weekStartText)
            ? ParseWeekday(weekStartText)
            : DayOfWeek.Monday;
        if (frequency == RecurrenceFrequency.Weekly &&
            byDays?.Any(day => day.Ordinal is not null) == true)
            throw new InvalidDataException("WEEKLY BYDAY values cannot use ordinals.");
        if (frequency == RecurrenceFrequency.Weekly && byMonthDays is not null)
            throw new InvalidDataException("WEEKLY rules cannot use BYMONTHDAY.");
        if (frequency is RecurrenceFrequency.Daily &&
            byDays?.Any(day => day.Ordinal is not null) == true)
            throw new InvalidDataException("DAILY BYDAY values cannot use ordinals.");
        if (frequency == RecurrenceFrequency.Yearly && byMonths is null &&
            (byDays is not null || byMonthDays is not null))
            throw new InvalidDataException(
                "YEARLY BYDAY or BYMONTHDAY requires an explicit BYMONTH boundary.");

        return new RecurrenceRule(
            frequency, count, until, interval, byDays, byMonthDays, byMonths, weekStart);
    }

    private static bool Matches(
        DateTime candidate,
        DateTime start,
        RecurrenceRule rule)
    {
        var frequencyMatches = rule.Frequency switch
        {
            RecurrenceFrequency.Daily =>
                (candidate.Date - start.Date).Days % rule.Interval == 0,
            RecurrenceFrequency.Weekly =>
                ((StartOfWeek(candidate.Date, rule.WeekStart) -
                  StartOfWeek(start.Date, rule.WeekStart)).Days / 7) % rule.Interval == 0,
            RecurrenceFrequency.Monthly =>
                ((candidate.Year - start.Year) * 12 + candidate.Month - start.Month) %
                    rule.Interval == 0,
            RecurrenceFrequency.Yearly =>
                (candidate.Year - start.Year) % rule.Interval == 0,
            _ => false,
        };
        if (!frequencyMatches) return false;
        if (rule.ByMonths is not null && !rule.ByMonths.Contains(candidate.Month)) return false;
        if (rule.Frequency == RecurrenceFrequency.Yearly &&
            rule.ByMonths is null && candidate.Month != start.Month)
            return false;
        if (rule.ByMonthDays is not null &&
            !rule.ByMonthDays.Any(day => ResolveMonthDay(candidate.Year, candidate.Month, day) ==
                candidate.Day))
            return false;

        if (rule.ByDays is not null)
        {
            if (!rule.ByDays.Any(day => WeekdayMatches(candidate, day))) return false;
        }
        else if (rule.Frequency == RecurrenceFrequency.Weekly &&
                 candidate.DayOfWeek != start.DayOfWeek)
        {
            return false;
        }
        else if (rule.Frequency is RecurrenceFrequency.Monthly or RecurrenceFrequency.Yearly &&
                 rule.ByMonthDays is null && candidate.Day != start.Day)
        {
            return false;
        }
        return candidate >= start;
    }

    private static bool WeekdayMatches(DateTime candidate, RecurrenceWeekday expected)
    {
        if (candidate.DayOfWeek != expected.DayOfWeek) return false;
        if (expected.Ordinal is null) return true;
        var daysInMonth = DateTime.DaysInMonth(candidate.Year, candidate.Month);
        var occurrence = (candidate.Day - 1) / 7 + 1;
        var reverseOccurrence = -((daysInMonth - candidate.Day) / 7 + 1);
        return expected.Ordinal.Value == occurrence || expected.Ordinal.Value == reverseOccurrence;
    }

    private static int ResolveMonthDay(int year, int month, int requested)
    {
        var days = DateTime.DaysInMonth(year, month);
        var resolved = requested > 0 ? requested : days + requested + 1;
        return resolved is >= 1 and <= 31 && resolved <= days ? resolved : 0;
    }

    private static DateTime StartOfWeek(DateTime date, DayOfWeek weekStart)
    {
        var delta = ((int)date.DayOfWeek - (int)weekStart + 7) % 7;
        return date.AddDays(-delta);
    }

    private static HashSet<int> ParseIntegerList(
        string value,
        int minimumMagnitude,
        int maximumMagnitude,
        bool allowNegative,
        string fieldName)
    {
        var result = new HashSet<int>();
        foreach (var item in value.Split(','))
        {
            if (!int.TryParse(
                    item, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                    out var parsed) || parsed == 0 || Math.Abs((long)parsed) < minimumMagnitude ||
                Math.Abs((long)parsed) > maximumMagnitude || (!allowNegative && parsed < 0) ||
                !result.Add(parsed))
                throw new InvalidDataException($"{fieldName} contains an invalid or duplicate value.");
        }
        return result;
    }

    private static List<RecurrenceWeekday> ParseWeekdays(string value)
    {
        var result = new List<RecurrenceWeekday>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in value.Split(','))
        {
            var item = raw.ToUpperInvariant();
            if (!seen.Add(item) || item.Length is < 2 or > 4)
                throw new InvalidDataException("BYDAY contains an invalid or duplicate value.");
            var dayText = item[^2..];
            var day = ParseWeekday(dayText);
            int? ordinal = null;
            if (item.Length > 2)
            {
                if (!int.TryParse(
                        item[..^2], NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out var parsedOrdinal) ||
                    parsedOrdinal is 0 or < -5 or > 5)
                    throw new InvalidDataException("BYDAY contains an invalid ordinal.");
                ordinal = parsedOrdinal;
            }
            result.Add(new RecurrenceWeekday(ordinal, day));
        }
        return result;
    }

    private static DayOfWeek ParseWeekday(string value) => value.ToUpperInvariant() switch
    {
        "SU" => DayOfWeek.Sunday,
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        _ => throw new InvalidDataException("A recurrence weekday is invalid."),
    };

    private enum RecurrenceFrequency
    {
        Daily,
        Weekly,
        Monthly,
        Yearly,
    }

    private sealed record RecurrenceRule(
        RecurrenceFrequency Frequency,
        int? Count,
        DateTime? UntilCoordinate,
        int Interval,
        IReadOnlyList<RecurrenceWeekday>? ByDays,
        HashSet<int>? ByMonthDays,
        HashSet<int>? ByMonths,
        DayOfWeek WeekStart);

    private readonly record struct RecurrenceWeekday(int? Ordinal, DayOfWeek DayOfWeek);
}
