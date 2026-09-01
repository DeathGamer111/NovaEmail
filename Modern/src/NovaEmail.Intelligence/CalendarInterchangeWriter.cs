using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NovaEmail.Intelligence;

public sealed record CalendarInterchangeDocument(
    string SuggestedFileName,
    byte[] Utf8Bytes,
    string Sha256,
    string Uid,
    bool UsesFloatingLocalTime,
    bool DurationInferred,
    bool RequiresUserConfirmation);

/// <summary>
/// Produces a bounded RFC 5545 event draft for explicit local export.
/// It cannot open a calendar, connect to a provider, or create an event.
/// </summary>
public static class CalendarInterchangeWriter
{
    public const int MaximumDocumentBytes = 65_536;
    private const int MaximumContentLineOctets = 75;

    public static CalendarInterchangeDocument Create(
        CalendarEvidenceSuggestion suggestion,
        DateTimeOffset generatedUtc)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (string.IsNullOrEmpty(suggestion.EvidenceId) ||
            suggestion.EvidenceId.Length != 64 ||
            suggestion.EvidenceId.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("Calendar evidence identity must be a lowercase SHA-256 value.");
        if (!suggestion.RequiresUserConfirmation)
            throw new InvalidDataException("Calendar interchange export requires a review-gated suggestion.");
        if (suggestion.StartLocal.Kind != DateTimeKind.Unspecified ||
            suggestion.StartLocal.Year is < 1970 or > 2100)
            throw new InvalidDataException("Calendar evidence must use a bounded floating local date and time.");

        var title = NormalizeText(suggestion.SuggestedTitle, 160, "Event from email");
        var evidence = NormalizeText(suggestion.EvidenceText, 512, "date evidence unavailable");
        var uid = $"{suggestion.EvidenceId}@novaemail.local.invalid";
        var start = suggestion.StartLocal;
        var end = suggestion.AllDay ? start.Date.AddDays(1) : start.AddHours(1);
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "PRODID:-//NovaEmail//Nova Email Development//EN",
            "VERSION:2.0",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "BEGIN:VEVENT",
            $"UID:{uid}",
            $"DTSTAMP:{generatedUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}",
        };
        if (suggestion.AllDay)
        {
            lines.Add($"DTSTART;VALUE=DATE:{start:yyyyMMdd}");
            lines.Add($"DTEND;VALUE=DATE:{end:yyyyMMdd}");
            lines.Add("X-NOVAEMAIL-INFERRED-DURATION:P1D");
        }
        else
        {
            lines.Add($"DTSTART:{start:yyyyMMdd'T'HHmmss}");
            lines.Add($"DTEND:{end:yyyyMMdd'T'HHmmss}");
            lines.Add("X-NOVAEMAIL-TIME-BASIS:FLOATING-LOCAL");
            lines.Add("X-NOVAEMAIL-INFERRED-DURATION:PT1H");
        }
        lines.Add($"SUMMARY:{EscapeText(title)}");
        lines.Add("DESCRIPTION:" + EscapeText(
            "NovaEmail local evidence suggestion. Review is required before import. " +
            $"Source evidence: {evidence}"));
        lines.Add($"X-NOVAEMAIL-EVIDENCE-ID:{suggestion.EvidenceId}");
        lines.Add("X-NOVAEMAIL-REVIEW-REQUIRED:TRUE");
        lines.Add($"X-NOVAEMAIL-AI-ENRICHED:{(suggestion.AiEnriched ? "TRUE" : "FALSE")}");
        lines.Add("STATUS:TENTATIVE");
        lines.Add("TRANSP:OPAQUE");
        lines.Add("END:VEVENT");
        lines.Add("END:VCALENDAR");

        var builder = new StringBuilder(2_048);
        foreach (var line in lines) AppendFoldedLine(builder, line);
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        if (bytes.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The calendar interchange draft exceeds its safety limit.");
        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"NovaEmail-{start:yyyyMMdd}-{suggestion.EvidenceId[..12]}.ics");
        return new CalendarInterchangeDocument(
            fileName,
            bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            uid,
            UsesFloatingLocalTime: !suggestion.AllDay,
            DurationInferred: true,
            RequiresUserConfirmation: true);
    }

    private static string NormalizeText(string? value, int maximum, string fallback)
    {
        var source = value ?? string.Empty;
        if (source.Length > maximum + 1)
        {
            source = source[..(maximum + 1)];
            if (char.IsHighSurrogate(source[^1])) source = source[..^1];
        }
        var normalized = source.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(Math.Min(normalized.Length, maximum + 1));
        var previousWasCarriageReturn = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > maximum) break;
            var character = rune.Value;
            if (character == '\r')
            {
                builder.Append('\n');
                previousWasCarriageReturn = true;
            }
            else if (character == '\n')
            {
                if (!previousWasCarriageReturn) builder.Append('\n');
                previousWasCarriageReturn = false;
            }
            else
            {
                previousWasCarriageReturn = false;
                if (character == '\t') builder.Append(' ');
                else if (!Rune.IsControl(rune)) builder.Append(rune);
            }
        }
        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    private static string EscapeText(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static void AppendFoldedLine(StringBuilder output, string line)
    {
        var segment = new StringBuilder(line.Length);
        var segmentOctets = 0;
        var segmentBudget = MaximumContentLineOctets;
        foreach (var rune in line.EnumerateRunes())
        {
            if (segmentOctets + rune.Utf8SequenceLength > segmentBudget)
            {
                output.Append(segment).Append("\r\n ");
                segment.Clear();
                segmentOctets = 0;
                segmentBudget = MaximumContentLineOctets - 1;
            }
            segment.Append(rune.ToString());
            segmentOctets += rune.Utf8SequenceLength;
        }
        output.Append(segment).Append("\r\n");
    }
}
