using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NovaEmail.Safety;

namespace NovaEmail.Storage;

internal static partial class StorageRootPolicy
{
    public static string EnsureSafeDatabasePath(string databasePath, string approvedDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedDataRoot);
        var root = TrimDirectorySeparator(ReparsePointPolicy.EnsureNoTraversal(
            approvedDataRoot, requireLeafExists: false));
        var path = ReparsePointPolicy.EnsureNoTraversal(
            databasePath, requireLeafExists: false);
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Modern database must be beneath its approved local data root.");
        }

        var pathRoot = Path.GetPathRoot(root)
            ?? throw new InvalidOperationException("Modern data root has no volume root.");
        var drive = new DriveInfo(pathRoot);
        if (drive.DriveType is DriveType.Network or DriveType.Removable or DriveType.CDRom or DriveType.NoRootDirectory)
        {
            throw new InvalidOperationException("Modern mail storage cannot be placed on network or removable media.");
        }

        RejectHardLinkedSqliteFiles(path);

        return path;
    }

    private static void RejectHardLinkedSqliteFiles(string databasePath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Modern mail storage requires Windows file-identity validation.");
        foreach (var candidate in new[]
                 {
                     databasePath,
                     databasePath + "-wal",
                     databasePath + "-shm",
                     databasePath + "-journal",
                 })
        {
            if (!File.Exists(candidate)) continue;
            var canonical = ReparsePointPolicy.EnsureNoTraversal(candidate, requireLeafExists: true);
            using var handle = File.OpenHandle(
                canonical, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
            if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Unable to validate the modern SQLite file identity.");
            if (information.NumberOfLinks != 1)
                throw new SecurityException(
                    "Modern SQLite files cannot be hard-linked to another path.");
        }
    }

    private static string TrimDirectorySeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);
    }
}
