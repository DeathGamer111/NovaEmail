namespace NovaEmail.Mail;

public enum MailSubmissionCertainty
{
    NotSubmitted,
    Unknown,
}

public sealed class MailDeliveryException : Exception
{
    public MailDeliveryException(
        MailSubmissionCertainty submissionCertainty,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        SubmissionCertainty = submissionCertainty;
    }

    public MailSubmissionCertainty SubmissionCertainty { get; }
}
