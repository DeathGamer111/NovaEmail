using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MimeKit;

namespace NovaEmail.Mail.Tests;

public sealed class SyntheticGoldenCorpusTests
{
    private static readonly string[] RequiredCategories =
    [
        "plaintext", "html", "multipart-alternative", "attachment", "multipart-nesting",
        "message-rfc822", "extended-charset", "quoted-printable", "base64-body",
        "malformed-tolerated", "large-attachment", "custom-headers", "reply",
        "reply-all", "threading", "forward", "receipt", "priority", "duplicate-message-id",
    ];

    [Fact]
    public async Task EveryManifestedGoldenMessageParsesWithinBoundsWithoutChangingSourceBytes()
    {
        var fixtureRoot = FindFixtureRoot();
        var manifestPath = Path.Combine(fixtureRoot, "golden-corpus.json");
        var entries = JsonSerializer.Deserialize<GoldenCorpusEntry[]>(
            await File.ReadAllTextAsync(manifestPath)) ?? [];
        Assert.NotEmpty(entries);
        Assert.Equal(entries.Length, entries.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count());

        var manifested = entries.Select(entry => entry.Path).Order(StringComparer.Ordinal).ToArray();
        var actual = Directory.GetFiles(fixtureRoot, "*.eml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(actual, manifested);
        var categories = entries.SelectMany(entry => entry.Categories).ToHashSet(StringComparer.Ordinal);
        Assert.All(RequiredCategories, category => Assert.Contains(category, categories));

        foreach (var entry in entries)
        {
            Assert.Equal(Path.GetFileName(entry.Path), entry.Path);
            var path = Path.Combine(fixtureRoot, entry.Path);
            var before = await File.ReadAllBytesAsync(path);
            var beforeHash = SHA256.HashData(before);
            var message = await SafeMimeParser.ParseAsync(before);
            Assert.Equal(entry.Subject, message.Subject);
            if (entry.Categories.Contains("custom-headers", StringComparer.Ordinal))
            {
                Assert.Equal("synthetic-corpus", message.Headers["X-NovaEmail-Fixture"]);
                Assert.Equal("preserve-verbatim", message.Headers["X-NovaEmail-Custom"]);
            }
            var after = await File.ReadAllBytesAsync(path);
            Assert.Equal(beforeHash, SHA256.HashData(after));
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public async Task DeterministicEightMiBAttachmentParsesAndExportsByteExactly()
    {
        const int payloadBytes = 8 * 1024 * 1024;
        var payload = new byte[payloadBytes];
        for (var index = 0; index < payload.Length; index++)
            payload[index] = unchecked((byte)(index * 31 + 17));
        var attachment = new MimePart("application", "octet-stream")
        {
            FileName = "eight-mib.bin",
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            Content = new MimeContent(new MemoryStream(payload, writable: false)),
        };
        var message = new MimeMessage
        {
            Subject = "Deterministic large attachment runtime corpus",
            Body = new Multipart("mixed")
            {
                new TextPart("plain") { Text = "Bounded large attachment." },
                attachment,
            },
        };
        message.From.Add(MailboxAddress.Parse("sender@novaemail.test"));
        message.To.Add(MailboxAddress.Parse("receiver@novaemail.test"));
        await using var serialized = new MemoryStream();
        await message.WriteToAsync(serialized);
        var wireBytes = serialized.ToArray();

        var parsed = await SafeMimeParser.ParseAsync(wireBytes);
        var descriptor = Assert.Single(SafeAttachmentExporter.Describe(parsed));
        Assert.Equal("eight-mib.bin", descriptor.FileName);
        Assert.True(wireBytes.Length > payloadBytes);
        await using var exported = new MemoryStream();
        var exportedBytes = await SafeAttachmentExporter.ExportAsync(
            parsed, descriptor.Ordinal, exported, payloadBytes);

        Assert.Equal(payloadBytes, exportedBytes);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(exported.ToArray()));
    }

    private static string FindFixtureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "TestLab", "fixtures", "synthetic");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Synthetic NovaEmail fixture root was not found.");
    }

    private sealed record GoldenCorpusEntry(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("categories")] string[] Categories);
}
