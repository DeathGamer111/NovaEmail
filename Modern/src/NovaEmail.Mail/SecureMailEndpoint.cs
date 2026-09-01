using MailKit.Security;

namespace NovaEmail.Mail;

public enum TlsConnectionMode
{
    ImplicitTls,
    RequiredStartTls,
}

public sealed record SecureMailEndpoint(
    string Host,
    int Port,
    TlsConnectionMode TlsMode)
{
    public SecureSocketOptions ToSecureSocketOptions() => TlsMode switch
    {
        TlsConnectionMode.ImplicitTls => SecureSocketOptions.SslOnConnect,
        TlsConnectionMode.RequiredStartTls => SecureSocketOptions.StartTls,
        _ => throw new ArgumentOutOfRangeException(
            nameof(TlsMode), TlsMode, "Unsupported TLS connection mode."),
    };
}

public sealed record MailCredentials
{
    private const int MaximumUserNameCharacters = 513;
    private const int MaximumPasswordCharacters = 256;

    public MailCredentials(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) ||
            userName.Length > MaximumUserNameCharacters ||
            userName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Mail credential user name is empty, contains control characters, or exceeds its limit.",
                nameof(userName));
        }
        if (string.IsNullOrWhiteSpace(password) ||
            password.Length > MaximumPasswordCharacters ||
            password.Contains('\0'))
        {
            throw new ArgumentException(
                "Mail credential password is empty, contains NUL, or exceeds its limit.",
                nameof(password));
        }

        UserName = userName;
        Password = password;
    }

    public string UserName { get; }
    public string Password { get; }

    public override string ToString() => "MailCredentials { <redacted> }";
}

public sealed record RemoteMessageDescriptor(
    string ProtocolIdentity,
    string? RfcMessageId,
    string Subject,
    string From)
{
    public bool HasMessageId(string expected) =>
        string.Equals(
            NormalizeMessageId(RfcMessageId),
            NormalizeMessageId(expected),
            StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeMessageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length >= 2 && normalized[0] == '<' && normalized[^1] == '>')
        {
            normalized = normalized[1..^1];
        }

        return normalized.Trim();
    }
}
