using MimeKit;

namespace NovaEmail.Mail;

public sealed record SafeMimeLimits
{
    public static SafeMimeLimits Default { get; } = new();

    public long MaximumMessageBytes { get; init; } = 100L * 1024L * 1024L;
    public int MaximumMimeDepth { get; init; } = 30;
    public int MaximumMimeEntities { get; init; } = 2_048;
    public int MaximumHeaders { get; init; } = 2_048;
    public int MaximumHeaderCharacters { get; init; } = 1 * 1024 * 1024;
    public int MaximumSingleHeaderCharacters { get; init; } = 64 * 1024;
    public int MaximumFileNameCharacters { get; init; } = 512;
    public int MaximumMailboxAddresses { get; init; } = 1_000;
    public TimeSpan ParseTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public static class SafeMimeParser
{
    private static readonly SemaphoreSlim ParseGate = new(1, 1);

    public static async Task<MimeMessage> ParseAsync(
        ReadOnlyMemory<byte> rawMime,
        CancellationToken cancellationToken = default) =>
        await ParseAsync(rawMime, SafeMimeLimits.Default, cancellationToken).ConfigureAwait(false);

    public static async Task<MimeMessage> ParseAsync(
        ReadOnlyMemory<byte> rawMime,
        SafeMimeLimits limits,
        CancellationToken cancellationToken = default)
    {
        ValidateLimits(limits);
        if (rawMime.IsEmpty) throw new InvalidDataException("MIME message cannot be empty.");
        if (rawMime.Length > limits.MaximumMessageBytes)
            throw new InvalidDataException("MIME message exceeds the configured byte limit.");
        cancellationToken.ThrowIfCancellationRequested();

        return await ParseOwnedBytesAsync(
            () => rawMime.ToArray(), limits, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MimeMessage> ParseOwnedBytesAsync(
        Func<byte[]> materializeBytes,
        SafeMimeLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(materializeBytes);
        var parseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        parseCancellation.CancelAfter(limits.ParseTimeout);
        var parseToken = parseCancellation.Token;
        var gateAcquired = false;
        var parseTaskOwnsCancellation = false;
        try
        {
            await ParseGate.WaitAsync(parseToken).ConfigureAwait(false);
            gateAcquired = true;
            var bytes = materializeBytes();
            var parseTask = Task.Run(() =>
            {
                try
                {
                    using var source = new MemoryStream(bytes, writable: false);
                    return MimeMessage.Load(source, persistent: false, parseToken);
                }
                finally
                {
                    ParseGate.Release();
                    parseCancellation.Dispose();
                }
            }, CancellationToken.None);
            gateAcquired = false;
            parseTaskOwnsCancellation = true;
            var message = await parseTask.WaitAsync(parseToken).ConfigureAwait(false);
            ValidateMessage(message, limits);
            return message;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidDataException("MIME parsing exceeded the configured processing-time limit.", exception);
        }
        finally
        {
            if (gateAcquired) ParseGate.Release();
            if (!parseTaskOwnsCancellation) parseCancellation.Dispose();
        }
    }

    public static async Task<MimeMessage> ParseAsync(
        Stream source,
        CancellationToken cancellationToken = default) =>
        await ParseAsync(source, SafeMimeLimits.Default, cancellationToken).ConfigureAwait(false);

    public static async Task<MimeMessage> ParseAsync(
        Stream source,
        SafeMimeLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateLimits(limits);
        if (!source.CanRead) throw new ArgumentException("MIME source must be readable.", nameof(source));
        if (source.CanSeek)
        {
            var remaining = checked(source.Length - source.Position);
            if (remaining <= 0) throw new InvalidDataException("MIME message cannot be empty.");
            if (remaining > limits.MaximumMessageBytes)
                throw new InvalidDataException("MIME message exceeds the configured byte limit.");
        }

        await using var retained = new MemoryStream();
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (retained.Length + read > limits.MaximumMessageBytes)
                throw new InvalidDataException("MIME message exceeds the configured byte limit.");
            await retained.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return await ParseOwnedBytesAsync(
            () => retained.ToArray(), limits, cancellationToken).ConfigureAwait(false);
    }

    public static void ValidateMessage(MimeMessage message, SafeMimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var effectiveLimits = limits ?? SafeMimeLimits.Default;
        ValidateLimits(effectiveLimits);
        var state = new ValidationState(effectiveLimits);
        state.ValidateHeaders(message.Headers);
        var addressCount = message.From.Mailboxes.Count() + message.ReplyTo.Mailboxes.Count() +
            message.To.Mailboxes.Count() + message.Cc.Mailboxes.Count() + message.Bcc.Mailboxes.Count();
        if (addressCount > effectiveLimits.MaximumMailboxAddresses)
            throw new InvalidDataException("MIME message exceeds the configured mailbox-address limit.");
        if (message.Body is not null) state.Visit(message.Body, depth: 1);
    }

    private static void ValidateLimits(SafeMimeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumMessageBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumMimeDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumMimeEntities, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumHeaders, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumHeaderCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumSingleHeaderCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumFileNameCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaximumMailboxAddresses, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limits.ParseTimeout, TimeSpan.Zero);
    }

    private sealed class ValidationState(SafeMimeLimits limits)
    {
        private readonly HashSet<MimeEntity> _visited = new(ReferenceEqualityComparer.Instance);
        private int _entityCount;
        private int _headerCount;
        private int _headerCharacters;

        public void Visit(MimeEntity entity, int depth)
        {
            if (depth > limits.MaximumMimeDepth)
                throw new InvalidDataException("MIME message exceeds the configured nesting-depth limit.");
            if (!_visited.Add(entity))
                throw new InvalidDataException("MIME message contains a cyclic entity graph.");
            _entityCount = checked(_entityCount + 1);
            if (_entityCount > limits.MaximumMimeEntities)
                throw new InvalidDataException("MIME message exceeds the configured entity-count limit.");
            ValidateHeaders(entity.Headers);
            ValidateFileName(entity);

            switch (entity)
            {
                case Multipart multipart:
                    foreach (var child in multipart) Visit(child, checked(depth + 1));
                    break;
                case MessagePart { Message: not null } messagePart:
                    ValidateHeaders(messagePart.Message.Headers);
                    if (messagePart.Message.Body is not null)
                        Visit(messagePart.Message.Body, checked(depth + 1));
                    break;
            }
        }

        public void ValidateHeaders(HeaderList headers)
        {
            foreach (var header in headers)
            {
                _headerCount = checked(_headerCount + 1);
                if (_headerCount > limits.MaximumHeaders)
                    throw new InvalidDataException("MIME message exceeds the configured header-count limit.");
                var characters = checked(header.Field.Length + header.Value.Length);
                if (characters > limits.MaximumSingleHeaderCharacters)
                    throw new InvalidDataException("MIME message contains an oversized header.");
                _headerCharacters = checked(_headerCharacters + characters);
                if (_headerCharacters > limits.MaximumHeaderCharacters)
                    throw new InvalidDataException("MIME message exceeds the configured total-header limit.");
            }
        }

        private void ValidateFileName(MimeEntity entity)
        {
            var fileName = entity.ContentDisposition?.FileName ?? entity.ContentType.Name;
            if (string.IsNullOrEmpty(fileName)) return;
            if (fileName.Length > limits.MaximumFileNameCharacters ||
                fileName.Any(character => character is '\r' or '\n' or '\0'))
                throw new InvalidDataException("MIME message contains an invalid or oversized attachment filename.");
        }
    }
}
