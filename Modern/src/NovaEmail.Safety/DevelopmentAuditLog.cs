using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaEmail.Safety;

public static partial class DevelopmentAuditLog
{
    private const long MaximumLogBytes = 1024L * 1024L;

    public static bool TryWrite(string eventId)
    {
        try
        {
            return WriteEvent(ApplicationIdentity.LogRoot, eventId, DateTimeOffset.UtcNow);
        }
        catch
        {
            // Logging must never weaken a security failure or crash recovery path.
            return false;
        }
    }

    internal static bool WriteEvent(string root, string eventId, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (!EventIdPattern().IsMatch(eventId))
            throw new ArgumentException(
                "Development audit event IDs may contain only uppercase ASCII letters, digits, and underscores.",
                nameof(eventId));
        var canonicalRoot = Path.GetFullPath(root);
        var existingAncestor = new DirectoryInfo(canonicalRoot);
        while (!existingAncestor.Exists)
        {
            existingAncestor = existingAncestor.Parent ??
                throw new InvalidOperationException("Development audit log root has no existing ancestor.");
        }
        ReparsePointPolicy.EnsureNoTraversal(existingAncestor.FullName, requireLeafExists: true);
        Directory.CreateDirectory(canonicalRoot);
        ReparsePointPolicy.EnsureNoTraversal(canonicalRoot, requireLeafExists: true);
        var logPath = Path.Combine(
            canonicalRoot,
            timestamp.UtcDateTime.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");
        var logInfo = new FileInfo(logPath);
        if (logInfo.Exists &&
            ((logInfo.Attributes & FileAttributes.ReparsePoint) != 0 || logInfo.Length >= MaximumLogBytes))
            return false;

        using var lineBuffer = new MemoryStream(192);
        using (var json = new Utf8JsonWriter(lineBuffer))
        {
            json.WriteStartObject();
            json.WriteString("utc", timestamp.ToUniversalTime());
            json.WriteString("event", eventId);
            json.WriteString("identity", ApplicationIdentity.PackageIdentityName);
            json.WriteEndObject();
        }
        lineBuffer.WriteByte((byte)'\n');
        if (logInfo.Exists && logInfo.Length > MaximumLogBytes - lineBuffer.Length) return false;
        using var output = new FileStream(
            logPath, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        lineBuffer.Position = 0;
        lineBuffer.CopyTo(output);
        output.Flush(flushToDisk: true);
        return true;
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventIdPattern();
}
