using System.Globalization;

namespace NovaEmail.Client;

public sealed class ComposeAttachmentItem
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public required byte[] Content { get; init; }

    public string Detail
    {
        get
        {
            var kilobytes = Content.LongLength / 1024d;
            return kilobytes < 1024
                ? $"{kilobytes.ToString("0.#", CultureInfo.CurrentCulture)} KB"
                : $"{(kilobytes / 1024d).ToString("0.#", CultureInfo.CurrentCulture)} MB";
        }
    }
}
