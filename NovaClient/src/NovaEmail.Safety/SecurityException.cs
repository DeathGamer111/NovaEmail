namespace NovaEmail.Safety;

public sealed class SecurityException : InvalidOperationException
{
    public SecurityException(string message)
        : base(message)
    {
    }
}
