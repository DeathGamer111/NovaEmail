using NovaEmail.Storage;

namespace NovaEmail.Sync;

public enum RemoteMailboxMutationDisposition
{
    RetryScheduled,
    Conflict,
}

public sealed class RemoteMailboxMutationException : Exception
{
    public RemoteMailboxMutationException(
        RemoteMailboxMutationDisposition disposition,
        string errorCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 128 ||
            errorCode.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Remote mutation error code is invalid.", nameof(errorCode));
        Disposition = disposition;
        ErrorCode = errorCode;
    }

    public RemoteMailboxMutationDisposition Disposition { get; }
    public string ErrorCode { get; }
}

public sealed record RemoteMailboxMutationAcknowledgement(string Value);

public interface IRemoteImapMutationSink
{
    Task<RemoteMailboxMutationAcknowledgement> ApplyAsync(
        StoredMailboxMutationOperation operation,
        CancellationToken cancellationToken = default);
}

public sealed record MailboxMutationDispatchResult(
    string OperationId,
    OperationEventState State,
    string? Detail,
    string? ErrorCode);

public sealed class MailboxMutationDispatcher
{
    private const string ImapAcknowledgedDetail =
        "IMAP server acknowledgement recorded; response detail was redacted.";
    private const string MutationRetryDetail =
        "The IMAP mutation did not start and may be retried only by an explicit request.";
    private const string MutationConflictDetail =
        "The IMAP mutation requires manual reconciliation; remote detail was redacted.";
    private const string UnexpectedMutationDetail =
        "The IMAP mutation outcome is unknown; automatic retry is prohibited.";
    private readonly ModernMailStore _store;
    private readonly IRemoteImapMutationSink _remote;

    public MailboxMutationDispatcher(ModernMailStore store, IRemoteImapMutationSink remote)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
    }

    public async Task<MailboxMutationDispatchResult?> DispatchNextAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await _store.MarkInterruptedMailboxMutationAttemptsAsConflictsAsync(cancellationToken)
            .ConfigureAwait(false);
        var operation = (await _store.ReadReadyMailboxMutationOperationsAsync(
            accountId, 1, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (operation is null) return null;

        if (operation.ExpectedUid is null || string.IsNullOrWhiteSpace(operation.ExpectedUidValidity))
        {
            const string errorCode = "MissingRemoteIdentity";
            await _store.AppendOperationEventAsync(
                operation.OperationId, OperationEventState.Conflict, errorCode: errorCode,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new MailboxMutationDispatchResult(
                operation.OperationId, OperationEventState.Conflict,
                "The queued local action has no stable IMAP UID and cannot be replayed safely.", errorCode);
        }

        await _store.AppendOperationEventAsync(
            operation.OperationId, OperationEventState.Attempting,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            var acknowledgement = await _remote.ApplyAsync(operation, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(acknowledgement.Value) || acknowledgement.Value.Length > 2_048)
                throw new InvalidDataException("The IMAP mutation acknowledgement is invalid.");
            await _store.AppendOperationEventAsync(
                operation.OperationId, OperationEventState.Acknowledged, ImapAcknowledgedDetail,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new MailboxMutationDispatchResult(
                operation.OperationId, OperationEventState.Acknowledged, ImapAcknowledgedDetail, null);
        }
        catch (RemoteMailboxMutationException exception)
        {
            var state = exception.Disposition == RemoteMailboxMutationDisposition.RetryScheduled
                ? OperationEventState.RetryScheduled
                : OperationEventState.Conflict;
            await _store.AppendOperationEventAsync(
                operation.OperationId, state, errorCode: exception.ErrorCode,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return new MailboxMutationDispatchResult(
                operation.OperationId,
                state,
                exception.Disposition == RemoteMailboxMutationDisposition.RetryScheduled
                    ? MutationRetryDetail
                    : MutationConflictDetail,
                exception.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            await _store.AppendOperationEventAsync(
                operation.OperationId, OperationEventState.Conflict,
                errorCode: "CanceledMutationOutcomeUnknown", cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            const string errorCode = "UnexpectedMutationOutcomeUnknown";
            await _store.AppendOperationEventAsync(
                operation.OperationId, OperationEventState.Conflict, errorCode: errorCode,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return new MailboxMutationDispatchResult(
                operation.OperationId, OperationEventState.Conflict, UnexpectedMutationDetail, errorCode);
        }
    }
}
