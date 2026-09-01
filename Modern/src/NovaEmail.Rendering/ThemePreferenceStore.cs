using System.Text.Json;

namespace NovaEmail.Rendering;

public sealed record ThemePreferenceLoadResult(
    ApplicationThemeSelection Selection,
    bool UsedFallback,
    string? Diagnostic);

public sealed class ThemePreferenceStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumSettingsBytes = 4 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly string _settingsPath;

    public ThemePreferenceStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public ThemePreferenceLoadResult Load()
    {
        try
        {
            EnsureNoReparseTraversal(_settingsPath, requireLeafExists: false);
            if (!File.Exists(_settingsPath))
                return new(ApplicationThemeSelection.System, UsedFallback: false, Diagnostic: null);
            EnsureNoReparseTraversal(_settingsPath, requireLeafExists: true);
            var file = new FileInfo(_settingsPath);
            if (file.Length is <= 0 or > MaximumSettingsBytes)
                return Fallback("Theme settings have an invalid size.");
            var bytes = File.ReadAllBytes(_settingsPath);
            var document = JsonSerializer.Deserialize<ThemePreferenceDocument>(bytes, JsonOptions);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion ||
                !ApplicationThemeCatalog.TryGetById(document.Selection, out var definition))
                return Fallback("Theme settings have an unsupported schema or selection.");
            return new(definition.Selection, UsedFallback: false, Diagnostic: null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or NotSupportedException)
        {
            return Fallback("Theme settings could not be read safely.");
        }
    }

    public void Save(ApplicationThemeSelection selection)
    {
        var definition = ApplicationThemeCatalog.Get(selection);
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("Theme settings require a parent directory.");
        EnsureNoReparseTraversal(directory, requireLeafExists: false);
        Directory.CreateDirectory(directory);
        EnsureNoReparseTraversal(directory, requireLeafExists: true);
        if (File.Exists(_settingsPath))
            EnsureNoReparseTraversal(_settingsPath, requireLeafExists: true);

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new ThemePreferenceDocument(CurrentSchemaVersion, definition.Id), JsonOptions);
        if (payload.Length > MaximumSettingsBytes)
            throw new InvalidOperationException("Theme settings exceed their storage limit.");

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

    private static ThemePreferenceLoadResult Fallback(string diagnostic) =>
        new(ApplicationThemeSelection.System, UsedFallback: true, diagnostic);

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
                throw new FileNotFoundException("Theme settings path does not exist.", canonical);
            if (attributes is not null && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Theme settings cannot traverse a reparse point.");

            var trimmed = Path.TrimEndingDirectorySeparator(current);
            var parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
            leaf = false;
        }
    }

    private sealed record ThemePreferenceDocument(int SchemaVersion, string Selection);
}
