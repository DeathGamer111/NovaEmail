using System.Text;
using System.Security.Cryptography;
using MimeKit;
using NovaEmail.Mail;

namespace NovaEmail.Mail.Tests;

public sealed class SafeMimeParserTests
{
    [Fact]
    public async Task ValidMessageParsesWithinAllBounds()
    {
        var bytes = "From: sender@example.test\r\nTo: recipient@example.test\r\n" +
            "Subject: bounded\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nbody\r\n";

        var message = await SafeMimeParser.ParseAsync(Encoding.ASCII.GetBytes(bytes));

        Assert.Equal("bounded", message.Subject);
        Assert.Equal("body\r\n", message.TextBody);
    }

    [Fact]
    public async Task EmptyAndOversizedInputsAreRejectedBeforeMimeParsing()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeMimeParser.ParseAsync(ReadOnlyMemory<byte>.Empty));
        var limits = SafeMimeLimits.Default with { MaximumMessageBytes = 16 };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeMimeParser.ParseAsync(new byte[17], limits));
        await using var stream = new MemoryStream(new byte[17], writable: false);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeMimeParser.ParseAsync(stream, limits));
    }

    [Fact]
    public async Task ExcessiveMimeNestingAndEntityCountsAreRejected()
    {
        MimeEntity nested = new TextPart("plain") { Text = "bottom" };
        for (var index = 0; index < 6; ++index)
            nested = new Multipart("mixed") { nested };
        var deeplyNested = new MimeMessage { Body = nested };
        var deeplyNestedBytes = await SerializeAsync(deeplyNested);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeMimeParser.ParseAsync(
                deeplyNestedBytes, SafeMimeLimits.Default with { MaximumMimeDepth = 4 }));

        var manyParts = new MimeMessage
        {
            Body = new Multipart("mixed")
            {
                new TextPart("plain") { Text = "one" },
                new TextPart("plain") { Text = "two" },
                new TextPart("plain") { Text = "three" },
            },
        };
        var manyPartBytes = await SerializeAsync(manyParts);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeMimeParser.ParseAsync(
                manyPartBytes, SafeMimeLimits.Default with { MaximumMimeEntities = 3 }));
    }

    [Fact]
    public async Task HeaderAddressAndAttachmentFilenameLimitsAreRejected()
    {
        var headers = new MimeMessage { Body = new TextPart("plain") { Text = "body" } };
        headers.Headers.Add("X-First", "one");
        headers.Headers.Add("X-Second", "two");
        var headerBytes = await SerializeAsync(headers);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeMimeParser.ParseAsync(
                headerBytes, SafeMimeLimits.Default with { MaximumHeaders = 1 }));

        var addresses = new MimeMessage { Body = new TextPart("plain") { Text = "body" } };
        addresses.To.Add(new MailboxAddress("One", "one@example.test"));
        addresses.Cc.Add(new MailboxAddress("Two", "two@example.test"));
        var addressBytes = await SerializeAsync(addresses);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeMimeParser.ParseAsync(
                addressBytes,
                SafeMimeLimits.Default with { MaximumMailboxAddresses = 1 }));

        var attachment = new MimeMessage
        {
            Body = new MimePart("application", "octet-stream")
            {
                Content = new MimeContent(new MemoryStream([1, 2, 3], writable: false)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                FileName = "filename-too-long.bin",
            },
        };
        var attachmentBytes = await SerializeAsync(attachment);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeMimeParser.ParseAsync(
                attachmentBytes,
                SafeMimeLimits.Default with { MaximumFileNameCharacters = 8 }));
    }

    [Fact]
    public async Task DeterministicMalformedMimeMutationsRemainBoundedAndNeverMutateInput()
    {
        var random = new Random(0x5043_4D45);
        var seed = Encoding.ASCII.GetBytes(
            "From: seed@example.test\r\nTo: target@example.test\r\n" +
            "Subject: mutation seed\r\nMIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=nova-seed\r\n\r\n" +
            "--nova-seed\r\nContent-Type: text/plain\r\n\r\nbody\r\n--nova-seed--\r\n");
        var limits = SafeMimeLimits.Default with
        {
            MaximumMessageBytes = 8 * 1024,
            ParseTimeout = TimeSpan.FromMilliseconds(500),
        };

        for (var iteration = 0; iteration < 256; iteration++)
        {
            byte[] input;
            if (iteration % 3 == 0)
            {
                input = seed.ToArray();
                var mutations = random.Next(1, 24);
                for (var mutation = 0; mutation < mutations; mutation++)
                    input[random.Next(input.Length)] = (byte)random.Next(0, 256);
            }
            else
            {
                input = new byte[random.Next(0, 4096)];
                random.NextBytes(input);
            }

            var before = SHA256.HashData(input);
            try
            {
                _ = await SafeMimeParser.ParseAsync(input, limits).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException)
            {
            }
            Assert.Equal(before, SHA256.HashData(input));
        }
    }

    private static async Task<byte[]> SerializeAsync(MimeMessage message)
    {
        await using var output = new MemoryStream();
        await message.WriteToAsync(output);
        return output.ToArray();
    }
}
