using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NovaEmail.Safety;

/// <summary>
/// An opaque, canonical destination that has passed the centralized managed
/// write-boundary policy. Callers cannot manufacture this capability from a
/// string.
/// </summary>
public sealed class ManagedWriteDestination
{
    internal ManagedWriteDestination(string approvedDataRoot) =>
        ApprovedDataRoot = approvedDataRoot;

    public string ApprovedDataRoot { get; }
}

/// <summary>
/// An opaque, canonical new file path approved by the same policy as managed
/// directory destinations.
/// </summary>
public sealed class ManagedWriteFile
{
    internal ManagedWriteFile(string approvedPath) => ApprovedPath = approvedPath;

    public string ApprovedPath { get; }
}

/// <summary>
/// An opaque, canonical directory approved for read-only test-fixture access.
/// Callers still enforce all per-request containment checks.
/// </summary>
public sealed class ApprovedReadOnlyRoot
{
    internal ApprovedReadOnlyRoot(string approvedRoot) => ApprovedRoot = approvedRoot;

    public string ApprovedRoot { get; }

    internal static ApprovedReadOnlyRoot AuthorizeExisting(string root)
    {
        var canonical = ManagedWriteDestinationPolicy.CanonicalizeLocalPath(
            root, requireLeafExists: true);
        if (!Directory.Exists(canonical))
            throw new DirectoryNotFoundException(
                $"Approved read-only root does not exist: {canonical}");
        EnsureFixedLocalVolume(canonical, "Approved read-only roots");
        return new ApprovedReadOnlyRoot(canonical);
    }

    private static void EnsureFixedLocalVolume(string path, string label)
    {
        var volumeRoot = Path.GetPathRoot(path)
            ?? throw new InvalidOperationException($"{label} require a volume root.");
        if (new DriveInfo(volumeRoot).DriveType != DriveType.Fixed)
            throw new SecurityException($"{label} must be on a fixed local volume.");
    }
}

/// <summary>
/// An opaque, fixed child of an already approved disposable fixture run. The
/// caller cannot provide the write destination: the only authorized location
/// is the test-store child derived here after the run marker and path identity
/// have been validated.
/// </summary>
public sealed class ApprovedFixtureStoreRoot
{
    internal const string DirectoryName = ".novaemail-test-store";

    internal ApprovedFixtureStoreRoot(string approvedDataRoot) =>
        ApprovedDataRoot = approvedDataRoot;

    public string ApprovedDataRoot { get; }

    internal static ApprovedFixtureStoreRoot AuthorizeFixedChild(
        ApprovedReadOnlyRoot fixtureRunRoot)
    {
        ArgumentNullException.ThrowIfNull(fixtureRunRoot);
        var runRoot = ManagedWriteDestinationPolicy.CanonicalizeLocalPath(
            fixtureRunRoot.ApprovedRoot, requireLeafExists: true);
        if (!Directory.Exists(runRoot))
            throw new DirectoryNotFoundException(
                "The approved disposable fixture run no longer exists.");

        var marker = Path.Combine(runRoot, ".novaemail-fixture-run.json");
        var canonicalMarker = ReparsePointPolicy.EnsureNoTraversal(
            marker, requireLeafExists: true);
        if (!File.Exists(canonicalMarker))
            throw new SecurityException(
                "A disposable test store requires the validated fixture-run marker.");

        var dataRoot = Path.GetFullPath(Path.Combine(runRoot, DirectoryName));
        var expected = runRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar + DirectoryName;
        if (!string.Equals(dataRoot, expected, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException(
                "The disposable test store escaped its fixed fixture-run child.");
        if (File.Exists(dataRoot))
            throw new SecurityException(
                "The disposable test store path is occupied by a file.");
        if (Directory.Exists(dataRoot))
        {
            dataRoot = ManagedWriteDestinationPolicy.CanonicalizeLocalPath(
                dataRoot, requireLeafExists: true);
            if (!string.Equals(dataRoot, expected, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException(
                    "The disposable test store resolved outside its fixed fixture-run child.");
        }
        else
        {
            dataRoot = ManagedWriteDestinationPolicy.CanonicalizeLocalPath(
                dataRoot, requireLeafExists: false);
            if (!string.Equals(dataRoot, expected, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException(
                    "The disposable test store resolved outside its fixed fixture-run child.");
        }

        return new ApprovedFixtureStoreRoot(dataRoot);
    }
}

/// <summary>
/// Central fail-closed policy for every user- or tool-selected managed write
/// destination. Built-in application and installation roots cannot be removed;
/// callers may add further source roots to protect.
/// </summary>
public sealed partial class ManagedWriteDestinationPolicy
{
    private readonly string[] _protectedRoots;

    private ManagedWriteDestinationPolicy(IEnumerable<string> protectedRoots)
    {
        _protectedRoots = protectedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => CanonicalizeLocalPath(root, requireLeafExists: false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static ManagedWriteDestinationPolicy CreateDevelopment(
        IEnumerable<string>? additionalProtectedRoots = null)
    {
        var roots = new List<string>
        {
            ApplicationIdentity.FixtureRoot,
            ApplicationIdentity.RunRoot,
            AppContext.BaseDirectory,
        };
        AddNovaEmailRoot(
            roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddNovaEmailRoot(
            roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddNovaEmailRoot(
            roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        AddIfPresent(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddIfPresent(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        if (additionalProtectedRoots is not null) roots.AddRange(additionalProtectedRoots);
        return new ManagedWriteDestinationPolicy(roots);
    }

    public ManagedWriteDestination ApproveNewRoot(string destinationRoot)
        => new(ApproveNewPath(destinationRoot));

    public ManagedWriteFile ApproveNewFile(string destinationFile)
        => new(ApproveNewPath(destinationFile));

    private string ApproveNewPath(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var canonical = CanonicalizeLocalPath(destinationPath, requireLeafExists: false);
        if (Directory.Exists(canonical) || File.Exists(canonical))
            throw new IOException(
                "Managed write destinations must be new; overwrite and in-place operation are prohibited.");

        if (_protectedRoots.Any(root => IsWithin(canonical, root) || IsWithin(root, canonical)))
            throw new SecurityException(
                "Managed writes cannot target or contain a protected application, installation, or source root.");

        var parent = Path.GetDirectoryName(canonical)
            ?? throw new InvalidOperationException("Managed write destination has no parent directory.");
        var canonicalParent = ReparsePointPolicy.EnsureNoTraversal(parent, requireLeafExists: true);
        if (!Directory.Exists(canonicalParent))
            throw new DirectoryNotFoundException(
                "Managed write destinations require an existing approved parent directory.");
        RejectProtectedMarkerAncestors(canonicalParent);
        EnsureFixedLocalVolume(canonicalParent);

        return canonical;
    }

    private static void EnsureFixedLocalVolume(string path)
    {
        var volumeRoot = Path.GetPathRoot(path)
            ?? throw new InvalidOperationException("Managed write destination has no volume root.");
        if (new DriveInfo(volumeRoot).DriveType != DriveType.Fixed)
            throw new SecurityException("Managed write destinations must be on a fixed local volume.");
    }

    private static bool IsWithin(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.StartsWith(
                   root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(
                   root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIfPresent(List<string> roots, string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
    }

    private static void AddNovaEmailRoot(List<string> roots, string parent)
    {
        if (!string.IsNullOrWhiteSpace(parent)) roots.Add(Path.Combine(parent, "NovaEmail"));
    }

    private static void RejectProtectedMarkerAncestors(string path)
    {
        string[] markers =
        [
            ".novaemail-fixture-run.json",
            ".novaemail-source-manifest.json",
        ];
        var current = Path.TrimEndingDirectorySeparator(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (markers.Any(marker => File.Exists(Path.Combine(current, marker))))
                throw new SecurityException(
                    "Managed writes cannot target a tree identified as protected application or fixture data.");
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
    }

    internal static string CanonicalizeLocalPath(string path, bool requireLeafExists)
    {
        ValidateLocalPathShape(path);
        var canonical = Path.TrimEndingDirectorySeparator(
            ReparsePointPolicy.EnsureNoTraversal(path, requireLeafExists));
        var nearestExisting = canonical;
        while (!Directory.Exists(nearestExisting) && !File.Exists(nearestExisting))
        {
            var parent = Path.GetDirectoryName(nearestExisting);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, nearestExisting, StringComparison.OrdinalIgnoreCase))
                throw new DirectoryNotFoundException(
                    "Managed paths require an existing local-volume ancestor.");
            nearestExisting = parent;
        }
        nearestExisting = ReparsePointPolicy.EnsureNoTraversal(
            nearestExisting, requireLeafExists: true);
        var longExisting = ExpandLongPath(nearestExisting);
        var relative = Path.GetRelativePath(nearestExisting, canonical);
        var resolved = relative.Equals(".", StringComparison.Ordinal)
            ? longExisting
            : Path.GetFullPath(Path.Combine(longExisting, relative));
        return Path.TrimEndingDirectorySeparator(
            ReparsePointPolicy.EnsureNoTraversal(resolved, requireLeafExists));
    }

    private static void ValidateLocalPathShape(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.IndexOf(':', 2) >= 0 ||
            path.IndexOfAny(['*', '?']) >= 0 ||
            path.Any(char.IsControl))
            throw new SecurityException(
                "Managed paths cannot use relative, UNC, device, extended, ADS, wildcard, or control forms.");
        var root = Path.GetPathRoot(path);
        if (root is null || root.Length < 3 || !char.IsAsciiLetter(root[0]) ||
            root[1] != ':' || root[2] is not ('\\' or '/'))
            throw new SecurityException("Managed paths require a fully qualified local drive path.");
        foreach (var segment in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.EndsWith(' ') || segment.EndsWith('.') || IsReservedDosName(segment))
                throw new SecurityException(
                    "Managed paths cannot use trailing-dot/space or reserved device-name segments.");
        }
    }

    private static bool IsReservedDosName(string segment)
    {
        var name = segment.Split('.', 2)[0];
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;
        return name.Length == 4 && name[3] is >= '1' and <= '9' &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static string ExpandLongPath(string existingPath)
    {
        if (!OperatingSystem.IsWindows()) return Path.GetFullPath(existingPath);
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new char[capacity];
            var length = NativeMethods.GetLongPathName(
                existingPath, buffer.AsSpan(), checked((uint)buffer.Length));
            if (length == 0)
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Unable to resolve the managed path's long filesystem identity.");
            if (length < buffer.Length)
                return Path.GetFullPath(new string(buffer, 0, checked((int)length)));
            capacity = checked((int)length + 1);
        }
        throw new PathTooLongException("Managed path identity exceeds the Windows path limit.");
    }

    private static partial class NativeMethods
    {
        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "GetLongPathNameW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        internal static partial uint GetLongPathName(
            string shortPath,
            Span<char> longPath,
            uint bufferCharacters);
    }
}
