namespace NovaEmail.Safety;

public static class ReparsePointPolicy
{
    public static string EnsureNoTraversal(string path, bool requireLeafExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Path.GetFullPath(path);
        return EnsureNoTraversalCore(canonical, requireLeafExists, TryReadAttributes);
    }

    internal static string EnsureNoTraversalCore(
        string canonicalPath,
        bool requireLeafExists,
        Func<string, FileAttributes?> readAttributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentNullException.ThrowIfNull(readAttributes);
        var canonical = Path.GetFullPath(canonicalPath);
        var current = canonical;
        var leaf = true;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var attributes = readAttributes(current);
            if (leaf && requireLeafExists && attributes is null)
                throw new FileNotFoundException("The approved path does not exist.", canonical);
            if (attributes is not null && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                throw new SecurityException($"Approved paths cannot traverse a reparse point: '{current}'.");

            var trimmed = Path.TrimEndingDirectorySeparator(current);
            var parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
            leaf = false;
        }
        return canonical;
    }

    private static FileAttributes? TryReadAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
