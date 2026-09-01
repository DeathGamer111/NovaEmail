using System.Globalization;

namespace NovaEmail.Client;

public sealed class MailItem
{
    public required string Id { get; init; }
    public required string Folder { get; init; }
    public required string Sender { get; init; }
    public required string Recipients { get; init; }
    public required string Subject { get; init; }
    public required string Preview { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    public string SenderInitial => string.IsNullOrWhiteSpace(Sender)
        ? "?"
        : char.ToUpperInvariant(Sender.Trim()[0]).ToString();

    public string DisplayTime => Timestamp.LocalDateTime.Date == DateTime.Today
        ? Timestamp.LocalDateTime.ToString("h:mm tt", CultureInfo.CurrentCulture)
        : Timestamp.LocalDateTime.ToString("MMM d", CultureInfo.CurrentCulture);
}
