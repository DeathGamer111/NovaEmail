using NovaEmail.Storage;
using System.Globalization;

namespace NovaEmail.Client;

public sealed class CalendarItem
{
    public required string EventId { get; init; }
    public required string Title { get; init; }
    public required DateTime StartLocal { get; init; }
    public required DateTime EndLocal { get; init; }
    public required bool AllDay { get; init; }
    public required string Location { get; init; }
    public required string Notes { get; init; }
    public required CalendarEventSourceKind SourceKind { get; init; }

    public string Day => StartLocal.ToString("dd", CultureInfo.CurrentCulture);
    public string Month => StartLocal.ToString("MMM", CultureInfo.CurrentCulture).ToUpperInvariant();
    public string DisplayRange => AllDay
        ? string.Format(CultureInfo.CurrentCulture, "{0:ddd, MMM d} · All day", StartLocal)
        : string.Format(
            CultureInfo.CurrentCulture,
            "{0:ddd, MMM d · h:mm tt}–{1:h:mm tt}",
            StartLocal,
            EndLocal);
    public string LocationText => string.IsNullOrWhiteSpace(Location) ? "No location" : Location;

    public static CalendarItem FromStored(StoredLocalCalendarEventRevision calendarEvent) => new()
    {
        EventId = calendarEvent.EventId,
        Title = calendarEvent.Title,
        StartLocal = calendarEvent.StartLocal,
        EndLocal = calendarEvent.EndLocal,
        AllDay = calendarEvent.AllDay,
        Location = calendarEvent.Location,
        Notes = calendarEvent.Notes,
        SourceKind = calendarEvent.SourceKind,
    };
}
