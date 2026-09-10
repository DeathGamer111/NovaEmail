using System.Security.Cryptography;
using System.Text;
using MimeKit;

namespace NovaEmail.Mail;

public sealed record MailAttachmentDescriptor(
    int Ordinal,
    string FileName,
    string MediaType,
    string ContentId = "");

public sealed record MaterializedMailAttachment(
    int Ordinal,
    string FileName,
    string MediaType,
    byte[] Content,
    string ContentSha256,
    string ContentId);

public static class SafeAttachmentExporter
{
    public const long DefaultMaximumDecodedBytes = 100L * 1024L * 1024L;
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static IReadOnlyList<MailAttachmentDescriptor> Describe(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return EnumerateRetainedEntities(message).Select((attachment, ordinal) =>
            DescribeEntity(attachment, ordinal))
            .ToArray();
    }

    public static async Task<IReadOnlyList<MaterializedMailAttachment>> MaterializeAsync(
        MimeMessage message,
        long maximumAggregateDecodedBytes = DefaultMaximumDecodedBytes,
        long maximumDecodedBytesPerAttachment = DefaultMaximumDecodedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAggregateDecodedBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDecodedBytesPerAttachment, 1);
        SafeMimeParser.ValidateMessage(message);

        var entities = EnumerateRetainedEntities(message);
        var materialized = new List<MaterializedMailAttachment>(entities.Length);
        long aggregateBytes = 0;
        for (var ordinal = 0; ordinal < entities.Length; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingAggregateBytes = maximumAggregateDecodedBytes - aggregateBytes;
            if (remainingAggregateBytes <= 0)
                throw new InvalidDataException(
                    $"Decoded attachments exceed the {maximumAggregateDecodedBytes:N0}-byte aggregate safety limit.");
            var maximumForThisAttachment = Math.Min(
                maximumDecodedBytesPerAttachment, remainingAggregateBytes);
            await using var output = new MemoryStream();
            long decodedBytes;
            try
            {
                decodedBytes = await ExportEntityAsync(
                    entities[ordinal], output, maximumForThisAttachment, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException exception) when (
                maximumForThisAttachment < maximumDecodedBytesPerAttachment)
            {
                throw new InvalidDataException(
                    $"Decoded attachments exceed the {maximumAggregateDecodedBytes:N0}-byte aggregate safety limit.",
                    exception);
            }
            aggregateBytes = checked(aggregateBytes + decodedBytes);
            var content = output.ToArray();
            var descriptor = DescribeEntity(entities[ordinal], ordinal);
            materialized.Add(new MaterializedMailAttachment(
                descriptor.Ordinal,
                descriptor.FileName,
                descriptor.MediaType,
                content,
                Convert.ToHexStringLower(SHA256.HashData(content)),
                descriptor.ContentId));
        }
        return materialized;
    }

    public static async Task<long> ExportAsync(
        MimeMessage message,
        int ordinal,
        Stream destination,
        long maximumDecodedBytes = DefaultMaximumDecodedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDecodedBytes, 1);
        if (!destination.CanWrite)
            throw new ArgumentException("Attachment destination must be writable.", nameof(destination));

        var attachment = EnumerateRetainedEntities(message).Skip(ordinal).FirstOrDefault()
            ?? throw new ArgumentOutOfRangeException(nameof(ordinal), "Attachment ordinal is outside this message.");
        return await ExportEntityAsync(
            attachment, destination, maximumDecodedBytes, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<long> ExportEntityAsync(
        MimeEntity entity,
        Stream destination,
        long maximumDecodedBytes = DefaultMaximumDecodedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDecodedBytes, 1);
        if (!destination.CanWrite)
            throw new ArgumentException("Attachment destination must be writable.", nameof(destination));

        await using var limited = new SizeLimitedWriteStream(destination, maximumDecodedBytes, leaveOpen: true);
        switch (entity)
        {
            case MimePart uuencodePart when
                uuencodePart.Content is not null &&
                uuencodePart.ContentTransferEncoding == ContentEncoding.UUEncode:
                await DecodeUuencodeAsync(uuencodePart, limited, cancellationToken).ConfigureAwait(false);
                break;
            case MimePart mimePart when mimePart.Content is not null:
                await mimePart.Content.DecodeToAsync(limited, cancellationToken).ConfigureAwait(false);
                break;
            case MessagePart { Message: not null } messagePart:
                await messagePart.Message.WriteToAsync(limited, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException("The selected MIME entity cannot be decoded safely.");
        }
        await limited.FlushAsync(cancellationToken).ConfigureAwait(false);
        return limited.BytesWritten;
    }

    private static async Task DecodeUuencodeAsync(
        MimePart part,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var content = part.Content ?? throw new InvalidDataException("UUEncoded MIME part has no content.");
        var source = content.Stream ?? throw new InvalidDataException("UUEncoded MIME part has no source stream.");
        if (!source.CanSeek)
            throw new InvalidDataException("UUEncoded MIME content must be seekable for bounded decoding.");

        var originalPosition = source.Position;
        source.Position = 0;
        try
        {
            using var reader = new StreamReader(
                source,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            var sawPayloadOrMarker = false;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.StartsWith("begin ", StringComparison.Ordinal))
                {
                    sawPayloadOrMarker = true;
                    continue;
                }
                if (string.Equals(line, "end", StringComparison.Ordinal)) break;
                if (line.Length == 0) continue;

                var expected = (line[0] - 32) & 63;
                sawPayloadOrMarker = true;
                if (expected == 0) continue;
                var requiredCharacters = 1 + ((expected + 2) / 3 * 4);
                if (line.Length < requiredCharacters)
                    throw new InvalidDataException("UUEncoded MIME entity is truncated.");

                var decoded = new byte[expected];
                var produced = 0;
                for (var index = 1; produced < expected; index += 4)
                {
                    var a = (line[index] - 32) & 63;
                    var b = (line[index + 1] - 32) & 63;
                    var c = (line[index + 2] - 32) & 63;
                    var d = (line[index + 3] - 32) & 63;
                    decoded[produced++] = (byte)((a << 2) | (b >> 4));
                    if (produced < expected) decoded[produced++] = (byte)((b << 4) | (c >> 2));
                    if (produced < expected) decoded[produced++] = (byte)((c << 6) | d);
                }
                await destination.WriteAsync(decoded, cancellationToken).ConfigureAwait(false);
            }

            if (!sawPayloadOrMarker)
                throw new InvalidDataException("UUEncoded MIME entity contains no payload.");
        }
        finally
        {
            source.Position = originalPosition;
        }
    }

    public static string SanitizeFileName(string? candidate, int ordinal)
    {
        var leaf = Path.GetFileName((candidate ?? string.Empty).Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(Math.Min(leaf.Length, 160));
        foreach (var character in leaf)
        {
            if (builder.Length == 160) break;
            builder.Append(character < ' ' || invalid.Contains(character) ? '_' : character);
        }
        var result = builder.ToString().Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(result)) result = $"attachment-{ordinal + 1}.bin";
        var stem = Path.GetFileNameWithoutExtension(result);
        if (ReservedDeviceNames.Contains(stem)) result = "_" + result;
        return result;
    }

    public static string NormalizeContentId(string? contentId)
    {
        var normalized = (contentId ?? string.Empty).Trim();
        if (normalized.Length >= 2 && normalized[0] == '<' && normalized[^1] == '>')
            normalized = normalized[1..^1].Trim();
        if (normalized.Length > 512 || normalized.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character) || character is '<' or '>'))
            throw new InvalidDataException("MIME Content-ID is invalid or exceeds its safety limit.");
        return normalized;
    }

    private static string? GetOriginalFileName(MimeEntity attachment) =>
        attachment.ContentDisposition?.FileName ?? attachment.ContentType.Name;

    private static MimeEntity[] EnumerateRetainedEntities(MimeMessage message) =>
        message.BodyParts
            .Where(entity => entity.IsAttachment ||
                !string.IsNullOrWhiteSpace(entity.ContentId) && entity is MimePart or MessagePart)
            .ToArray();

    private static MailAttachmentDescriptor DescribeEntity(MimeEntity attachment, int ordinal) =>
        new(
            ordinal,
            SanitizeFileName(GetOriginalFileName(attachment), ordinal),
            attachment.ContentType.MimeType,
            NormalizeContentId(attachment.ContentId));

    private sealed class SizeLimitedWriteStream(Stream inner, long maximumBytes, bool leaveOpen) : Stream
    {
        private long _bytesWritten;

        public long BytesWritten => _bytesWritten;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWithinLimit(count);
            inner.Write(buffer, offset, count);
            _bytesWritten += count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureWithinLimit(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _bytesWritten += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen) inner.Dispose();
            base.Dispose(disposing);
        }

        private void EnsureWithinLimit(int nextBytes)
        {
            if (nextBytes < 0 || _bytesWritten > maximumBytes - nextBytes)
                throw new InvalidDataException($"Decoded attachment exceeds the {maximumBytes:N0}-byte safety limit.");
        }
    }
}
