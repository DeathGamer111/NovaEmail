using System.Collections.ObjectModel;
using System.Text.Json;

namespace NovaEmail.Storage;

public sealed record MailboxMessagePresentationState(
    string Folder,
    bool IsRead,
    bool IsFollowUp);

public sealed record MailboxLabelDefinition(
    string Id,
    string DisplayName,
    string FilterText);

public sealed record MailboxPresentationState(
    IReadOnlyDictionary<string, MailboxMessagePresentationState> Messages,
    IReadOnlyList<MailboxLabelDefinition> Labels)
{
    public static MailboxPresentationState Empty { get; } = new(
        new ReadOnlyDictionary<string, MailboxMessagePresentationState>(
            new Dictionary<string, MailboxMessagePresentationState>(StringComparer.Ordinal)),
        Array.Empty<MailboxLabelDefinition>());
}

public sealed record MailboxPresentationStateLoadResult(
    MailboxPresentationState State,
    bool UsedFallback,
    string? Diagnostic);

public sealed class MailboxPresentationStateStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumSettingsBytes = 2 * 1024 * 1024;
    public const int MaximumMessageStates = 20_000;
    public const int MaximumLabels = 100;
    public const int MaximumMessageIdLength = 512;
    public const int MaximumFolderLength = 64;
    public const int MaximumLabelIdLength = 64;
    public const int MaximumLabelNameLength = 60;
    public const int MaximumLabelFilterLength = 160;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public MailboxPresentationStateStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public MailboxPresentationStateLoadResult Load()
    {
        try
        {
            EnsureNoReparseTraversal(_settingsPath, requireLeafExists: false);
            if (!File.Exists(_settingsPath))
                return new(MailboxPresentationState.Empty, UsedFallback: false, Diagnostic: null);
            EnsureNoReparseTraversal(_settingsPath, requireLeafExists: true);
            var file = new FileInfo(_settingsPath);
            if (file.Length is <= 0 or > MaximumSettingsBytes)
                return Fallback("Mailbox presentation settings have an invalid size.");

            var document = JsonSerializer.Deserialize<MailboxPresentationStateDocument>(
                File.ReadAllBytes(_settingsPath), JsonOptions);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion)
                return Fallback("Mailbox presentation settings have an unsupported schema.");
            return new(ToValidatedState(document), UsedFallback: false, Diagnostic: null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or NotSupportedException or InvalidDataException)
        {
            return Fallback("Mailbox presentation settings could not be read safely.");
        }
    }

    public void Save(MailboxPresentationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var validated = ValidateAndCopy(state.Messages, state.Labels);
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("Mailbox presentation settings require a parent directory.");
        EnsureNoReparseTraversal(directory, requireLeafExists: false);
        Directory.CreateDirectory(directory);
        EnsureNoReparseTraversal(directory, requireLeafExists: true);
        if (File.Exists(_settingsPath))
            EnsureNoReparseTraversal(_settingsPath, requireLeafExists: true);

        var document = new MailboxPresentationStateDocument(
            CurrentSchemaVersion,
            validated.Messages.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            validated.Labels.ToList());
        var payload = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (payload.Length > MaximumSettingsBytes)
            throw new InvalidOperationException("Mailbox presentation settings exceed their storage limit.");

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            EnsureNoReparseTraversal(temporaryPath, requireLeafExists: true);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static MailboxPresentationState ToValidatedState(MailboxPresentationStateDocument document) =>
        ValidateAndCopy(
            document.Messages ?? new Dictionary<string, MailboxMessagePresentationState>(),
            document.Labels ?? []);

    private static MailboxPresentationState ValidateAndCopy(
        IReadOnlyDictionary<string, MailboxMessagePresentationState> messages,
        IReadOnlyList<MailboxLabelDefinition> labels)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(labels);
        if (messages.Count > MaximumMessageStates)
            throw new InvalidDataException("Mailbox presentation settings contain too many message states.");
        if (labels.Count > MaximumLabels)
            throw new InvalidDataException("Mailbox presentation settings contain too many labels.");

        var messageCopy = new Dictionary<string, MailboxMessagePresentationState>(
            messages.Count, StringComparer.Ordinal);
        foreach (var (messageId, state) in messages)
        {
            ValidateText(messageId, MaximumMessageIdLength, allowEmpty: false, "message ID");
            if (state is null)
                throw new InvalidDataException("Mailbox presentation settings contain a null message state.");
            ValidateText(state.Folder, MaximumFolderLength, allowEmpty: true, "folder");
            if (!messageCopy.TryAdd(messageId, state))
                throw new InvalidDataException("Mailbox presentation settings contain duplicate message IDs.");
        }

        var labelCopy = new List<MailboxLabelDefinition>(labels.Count);
        var labelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (label is null)
                throw new InvalidDataException("Mailbox presentation settings contain a null label.");
            ValidateText(label.Id, MaximumLabelIdLength, allowEmpty: false, "label ID");
            ValidateText(label.DisplayName, MaximumLabelNameLength, allowEmpty: false, "label name");
            ValidateText(label.FilterText, MaximumLabelFilterLength, allowEmpty: true, "label filter");
            if (!labelIds.Add(label.Id))
                throw new InvalidDataException("Mailbox presentation settings contain duplicate label IDs.");
            labelCopy.Add(label);
        }

        return new(
            new ReadOnlyDictionary<string, MailboxMessagePresentationState>(messageCopy),
            labelCopy.AsReadOnly());
    }

    private static void ValidateText(string? value, int maximumLength, bool allowEmpty, string field)
    {
        if (value is null || !allowEmpty && string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => char.IsControl(character)))
            throw new InvalidDataException($"Mailbox presentation settings contain an invalid {field}.");
    }

    private static MailboxPresentationStateLoadResult Fallback(string diagnostic) =>
        new(MailboxPresentationState.Empty, UsedFallback: true, diagnostic);

    private static void EnsureNoReparseTraversal(string path, bool requireLeafExists)
    {
        var canonical = Path.GetFullPath(path);
        var current = canonical;
        var leaf = true;
        while (!string.IsNullOrWhiteSpace(current))
        {
            FileAttributes? attributes = null;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            if (leaf && requireLeafExists && attributes is null)
                throw new FileNotFoundException("Mailbox presentation settings path does not exist.", canonical);
            if (attributes is not null && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Mailbox presentation settings cannot traverse a reparse point.");

            var trimmed = Path.TrimEndingDirectorySeparator(current);
            var parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
            leaf = false;
        }
    }

    private sealed record MailboxPresentationStateDocument(
        int SchemaVersion,
        Dictionary<string, MailboxMessagePresentationState>? Messages,
        List<MailboxLabelDefinition>? Labels);
}
