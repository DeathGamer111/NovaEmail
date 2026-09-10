using System.Diagnostics;
using System.Net;
using MimeKit;
using NovaEmail.Safety;
using NovaEmail.Testing;

namespace NovaEmail.Mail.Tests;

public sealed class ScriptedProtocolFailureTests
{
    [Fact]
    public async Task UntrustedTlsCertificateIsRejectedBeforeSmtpCommands()
    {
        await using var server = ScriptedSmtpsServer.Start(ScriptedSmtpScenario.Accept);
        var transport = new SecureMailTransport(new EndpointPolicy(), new CertificateTrustPolicy());

        var failure = await Assert.ThrowsAsync<MailDeliveryException>(() => transport.SendAsync(
            Endpoint(server), Credentials, CreateMessage()));

        Assert.Equal(MailSubmissionCertainty.NotSubmitted, failure.SubmissionCertainty);
        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(0, server.MessageCount);
    }

    [Fact]
    public async Task AuthenticationRejectionIsNotClassifiedAsASubmission()
    {
        await using var server = ScriptedSmtpsServer.Start(ScriptedSmtpScenario.RejectAuthentication);
        var transport = TrustedTransport(server);

        var failure = await Assert.ThrowsAsync<MailDeliveryException>(() => transport.SendAsync(
            Endpoint(server), Credentials, CreateMessage()));

        Assert.Equal(MailSubmissionCertainty.NotSubmitted, failure.SubmissionCertainty);
        Assert.Equal(0, server.MessageCount);
    }

    [Fact]
    public async Task MalformedSmtpReplyIsRejectedBeforeSubmission()
    {
        await using var server = ScriptedSmtpsServer.Start(ScriptedSmtpScenario.MalformedEhloResponse);
        var transport = TrustedTransport(server);

        var failure = await Assert.ThrowsAsync<MailDeliveryException>(() => transport.SendAsync(
            Endpoint(server), Credentials, CreateMessage()));

        Assert.Equal(MailSubmissionCertainty.NotSubmitted, failure.SubmissionCertainty);
        Assert.Equal(0, server.MessageCount);
    }

    [Fact]
    public async Task SilentServerIsStoppedByTheBoundedTransportTimeout()
    {
        await using var server = ScriptedSmtpsServer.Start(ScriptedSmtpScenario.StallBeforeGreeting);
        var transport = TrustedTransport(server, timeoutMilliseconds: 250);
        var stopwatch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<MailDeliveryException>(() => transport.SendAsync(
            Endpoint(server), Credentials, CreateMessage()));

        Assert.Equal(MailSubmissionCertainty.NotSubmitted, failure.SubmissionCertainty);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Timeout took {stopwatch.Elapsed}.");
        Assert.Equal(0, server.MessageCount);
    }

    [Fact]
    public async Task DisconnectAfterDataIsReportedAsUnknownInsteadOfSafeToRetry()
    {
        await using var server = ScriptedSmtpsServer.Start(ScriptedSmtpScenario.DisconnectAfterData);
        var transport = TrustedTransport(server);
        var message = CreateMessage();

        var failure = await Assert.ThrowsAsync<MailDeliveryException>(() => transport.SendAsync(
            Endpoint(server), Credentials, message));

        Assert.True(
            failure.SubmissionCertainty == MailSubmissionCertainty.Unknown,
            $"Expected an unknown submission outcome, got {failure.SubmissionCertainty}. " +
            $"Commands: {string.Join(" | ", server.Commands)}. " +
            $"Server failure: {server.LastServerException}. Client failure: {failure}");
        Assert.Equal(1, server.MessageCount);
        Assert.Contains(message.MessageId!, server.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransportConnectsUsingOneApprovedLoopbackResolutionSnapshot()
    {
        await using var server = ScriptedSmtpsServer.Start(ScriptedSmtpScenario.Accept);
        var resolutionCount = 0;
        var policy = new EndpointPolicy(_ =>
        {
            var count = Interlocked.Increment(ref resolutionCount);
            return count == 1
                ? [IPAddress.Loopback]
                : [IPAddress.Parse("203.0.113.11")];
        });
        var transport = new SecureMailTransport(
            policy, new CertificateTrustPolicy([server.Certificate]));

        await transport.SendAsync(Endpoint(server), Credentials, CreateMessage());

        Assert.Equal(1, resolutionCount);
        Assert.Equal(1, server.MessageCount);
    }

    private static SecureMailTransport TrustedTransport(
        ScriptedSmtpsServer server,
        int timeoutMilliseconds = 30_000) => new(
            new EndpointPolicy(), new CertificateTrustPolicy([server.Certificate]), timeoutMilliseconds);

    private static SecureMailEndpoint Endpoint(ScriptedSmtpsServer server) =>
        new("localhost", server.Port, TlsConnectionMode.ImplicitTls);

    private static readonly MailCredentials Credentials = new("scripted-user", "scripted-password");

    private static MimeMessage CreateMessage()
    {
        var message = new MimeMessage
        {
            MessageId = $"{Guid.NewGuid():N}@novaemail.test",
            Subject = "NovaEmail scripted transport fault probe",
            Body = new TextPart("plain") { Text = "The protocol double is local and deterministic." },
        };
        message.From.Add(MailboxAddress.Parse("sender@novaemail.test"));
        message.To.Add(MailboxAddress.Parse("receiver@novaemail.test"));
        return message;
    }
}
