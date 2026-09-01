using System.Globalization;
using NovaEmail.Storage;

namespace NovaEmail.Client;

public sealed class AttachmentItem
{
    public required string MessageId { get; init; }
    public required int Ordinal { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public required long ByteCount { get; init; }

    public string Detail => $"{MediaType} · {FormatSize(ByteCount)}";

    public static AttachmentItem FromStored(StoredAttachmentSummary stored) => new()
    {
        MessageId = stored.MessageId,
        Ordinal = stored.Ordinal,
        FileName = stored.FileName,
        MediaType = stored.MediaType,
        ByteCount = stored.ByteCount,
    };

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes.ToString(CultureInfo.CurrentCulture)} B";
        var kilobytes = bytes / 1024d;
        if (kilobytes < 1024) return $"{kilobytes.ToString("0.#", CultureInfo.CurrentCulture)} KB";
        return $"{(kilobytes / 1024d).ToString("0.#", CultureInfo.CurrentCulture)} MB";
    }
}
