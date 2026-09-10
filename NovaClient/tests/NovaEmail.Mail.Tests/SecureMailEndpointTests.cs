using MailKit.Security;

namespace NovaEmail.Mail.Tests;

public sealed class SecureMailEndpointTests
{
    [Theory]
    [InlineData(TlsConnectionMode.ImplicitTls, SecureSocketOptions.SslOnConnect)]
    [InlineData(TlsConnectionMode.RequiredStartTls, SecureSocketOptions.StartTls)]
    public void SupportedTlsModeMapsWithoutDowngrade(
        TlsConnectionMode mode,
        SecureSocketOptions expected)
    {
        var endpoint = new SecureMailEndpoint("localhost", 1, mode);

        Assert.Equal(expected, endpoint.ToSecureSocketOptions());
    }

    [Fact]
    public void UnknownTlsModeFailsBeforeTransportConnection()
    {
        var endpoint = new SecureMailEndpoint(
            "localhost", 1, (TlsConnectionMode)int.MaxValue);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => endpoint.ToSecureSocketOptions());
        Assert.Contains("Unsupported TLS connection mode", exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MailCredentialsRejectMalformedValuesAndRedactFormatting()
    {
        Assert.Throws<ArgumentException>(() => new MailCredentials(string.Empty, "password"));
        Assert.Throws<ArgumentException>(() => new MailCredentials("user\r\nname", "password"));
        Assert.Throws<ArgumentException>(() => new MailCredentials("user", "   "));
        Assert.Throws<ArgumentException>(() => new MailCredentials("user", "before\0after"));
        Assert.Throws<ArgumentException>(() => new MailCredentials("user", new string('x', 257)));

        var credential = new MailCredentials(
            "format-user-" + Guid.NewGuid().ToString("N"),
            "format-password-" + Guid.NewGuid().ToString("N"));
        var formatted = credential.ToString();

        Assert.DoesNotContain(credential.UserName, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.Password, formatted, StringComparison.Ordinal);
        Assert.Contains("redacted", formatted, StringComparison.OrdinalIgnoreCase);
    }
}
