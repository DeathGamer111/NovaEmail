using System.Security.Cryptography;
using MimeKit;
using NovaEmail.Mail;

namespace NovaEmail.Mail.Tests;

public sealed class SafeAttachmentExporterTests
{
    [Fact]
    public async Task ExportDecodesExactlyAndLeavesDestinationOpen()
    {
        var expected = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        var message = CreateMessage("report.bin", expected);
        await using var destination = new MemoryStream();

        var bytes = await SafeAttachmentExporter.ExportAsync(message, 0, destination);

        Assert.Equal(expected.Length, bytes);
        Assert.Equal(expected, destination.ToArray());
        Assert.True(destination.CanWrite);
    }

    [Fact]
    public async Task ExportRejectsDecodedAttachmentBeyondLimit()
    {
        var message = CreateMessage("oversized.bin", new byte[4097]);
        await using var destination = new MemoryStream();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeAttachmentExporter.ExportAsync(message, 0, destination, maximumDecodedBytes: 4096));

        Assert.Contains("safety limit", error.Message, StringComparison.Ordinal);
        Assert.True(destination.Length <= 4096);
    }

    [Fact]
    public async Task MaterializeSanitizesNamesAndHashesExactDecodedBytes()
    {
        var expected = "normalized attachment bytes"u8.ToArray();
        var message = CreateMessage("../../evidence.txt", expected);

        var attachment = Assert.Single(await SafeAttachmentExporter.MaterializeAsync(message));

        Assert.Equal(0, attachment.Ordinal);
        Assert.Equal("evidence.txt", attachment.FileName);
        Assert.Equal("application/octet-stream", attachment.MediaType);
        Assert.Equal(expected, attachment.Content);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), attachment.ContentSha256);
        Assert.Equal("inline-attachment@example.test", attachment.ContentId);
    }

    [Fact]
    public async Task MaterializeRejectsAggregateDecodedAttachmentOverflow()
    {
        var message = CreateMessage("first.bin", [1, 2, 3]);
        Assert.IsType<Multipart>(message.Body).Add(CreateAttachment("second.bin", [4, 5, 6]));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeAttachmentExporter.MaterializeAsync(
                message, maximumAggregateDecodedBytes: 5, maximumDecodedBytesPerAttachment: 5));

        Assert.Contains("aggregate safety limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterializeRetainsContentIdInlinePartWithoutAttachmentDisposition()
    {
        var bytes = new byte[] { 9, 8, 7, 6 };
        var inline = new MimePart("image", "png")
        {
            Content = new MimeContent(new MemoryStream(bytes, writable: false)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
            ContentTransferEncoding = ContentEncoding.Base64,
            ContentId = "inline-image@example.test",
        };
        var message = new MimeMessage
        {
            Body = new MultipartRelated
            {
                new TextPart("html") { Text = "<img src=\"cid:inline-image@example.test\">" },
                inline,
            },
        };

        var retained = Assert.Single(await SafeAttachmentExporter.MaterializeAsync(message));

        Assert.Equal("attachment-1.bin", retained.FileName);
        Assert.Equal("image/png", retained.MediaType);
        Assert.Equal("inline-image@example.test", retained.ContentId);
        Assert.Equal(bytes, retained.Content);
    }

    [Theory]
    [InlineData("../../secret.txt", "secret.txt")]
    [InlineData("..\\..\\CON", "_CON")]
    [InlineData("", "attachment-3.bin")]
    public void FileNamesAreLeafOnlyAndWindowsSafe(string candidate, string expected)
    {
        Assert.Equal(expected, SafeAttachmentExporter.SanitizeFileName(candidate, 2));
    }

    private static MimeMessage CreateMessage(string fileName, byte[] bytes)
    {
        return new MimeMessage
        {
            Subject = "Attachment test",
            Body = new Multipart("mixed")
            {
                new TextPart("plain") { Text = "Attachment follows." },
                CreateAttachment(fileName, bytes),
            },
        };
    }

    private static MimePart CreateAttachment(string fileName, byte[] bytes) =>
        new("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream(bytes, writable: false)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = fileName,
            ContentId = "inline-attachment@example.test",
        };
}
