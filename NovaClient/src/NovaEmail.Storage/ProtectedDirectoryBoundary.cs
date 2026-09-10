using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NovaEmail.Safety;

namespace NovaEmail.Storage;

internal interface IProtectedDirectoryBoundary
{
    IProtectedDirectoryLease CreateExclusive(string path);
}

internal interface IProtectedDirectoryLease : IDisposable
{
    string CurrentPath { get; }

    void Verify(string expectedPath);

    void Publish(string destinationPath);

    bool CleanupIfOwned();
}

internal sealed partial class WindowsProtectedDirectoryBoundary : IProtectedDirectoryBoundary
{
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileTraverse = 0x00000020;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const int ErrorAlreadyExists = 183;

    public static WindowsProtectedDirectoryBoundary Instance { get; } = new();

    private WindowsProtectedDirectoryBoundary()
    {
    }

    public IProtectedDirectoryLease CreateExclusive(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Protected backup/restore directories require Windows identity handles and ACLs.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.GetDirectoryName(canonical)
            ?? throw new InvalidOperationException("Protected staging directory has no parent.");
        ReparsePointPolicy.EnsureNoTraversal(parent, requireLeafExists: true);
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("Protected staging parent does not exist.");

        var security = CreateCurrentUserSecurity();
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptorPointer,
                InheritHandle = 0,
            };
            if (!NativeMethods.CreateDirectory(canonical, in attributes))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorAlreadyExists)
                    throw new IOException(
                        "Protected staging directory already exists; exclusive creation is required.");
                throw new Win32Exception(error, "Unable to create the protected staging directory.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptorPointer);
        }

        var handle = OpenIdentityHandle(
            canonical, includeDeleteAccess: true, shareDelete: false);
        WindowsProtectedDirectoryLease? lease = null;
        try
        {
            var identity = ReadIdentity(handle);
            lease = new WindowsProtectedDirectoryLease(canonical, handle, identity);
            handle = null!;
            lease.Verify(canonical);
            var acl = CurrentUserOnlyAclProbe.InspectRootOnly(canonical);
            if (!acl.Passed)
                throw new SecurityException(
                    "The exclusively created staging directory did not retain its final protected DACL.");
            return lease;
        }
        catch
        {
            if (lease is not null)
            {
                _ = lease.CleanupIfOwned();
                lease.Dispose();
            }
            else
            {
                handle.Dispose();
            }
            throw;
        }
    }

    private static DirectorySecurity CreateCurrentUserSecurity()
    {
        var currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetOwner(currentSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    private static SafeFileHandle OpenIdentityHandle(
        string path,
        bool includeDeleteAccess,
        bool shareDelete)
    {
        var desiredAccess = FileReadAttributes | ReadControl |
            (includeDeleteAccess ? DeleteAccess : FileTraverse | SynchronizeAccess);
        var share = FileShareRead | FileShareWrite |
            (shareDelete ? FileShareDelete : 0);
        var handle = NativeMethods.CreateFile(
            path,
            desiredAccess,
            share,
            0,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(
            error, $"Unable to open the protected directory identity handle (Win32 {error}).");
    }

    private static DirectoryIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Unable to read the protected directory identity.");
        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException(
                "Protected staging identity must be a non-reparse directory.");
        return new DirectoryIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private sealed class WindowsProtectedDirectoryLease(
        string currentPath,
        SafeFileHandle handle,
        DirectoryIdentity identity) : IProtectedDirectoryLease
    {
        private SafeFileHandle? _handle = handle;
        private readonly DirectoryIdentity _identity = identity;
        private bool _published;

        public string CurrentPath { get; private set; } = currentPath;

        public void Verify(string expectedPath)
        {
            var activeHandle = _handle
                ?? throw new ObjectDisposedException(nameof(WindowsProtectedDirectoryLease));
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath));
            if (!string.Equals(canonical, CurrentPath, StringComparison.OrdinalIgnoreCase) ||
                ReadIdentity(activeHandle) != _identity)
                throw new SecurityException("Protected staging directory identity changed.");
            using var pathHandle = OpenIdentityHandle(
                canonical, includeDeleteAccess: false, shareDelete: true);
            if (ReadIdentity(pathHandle) != _identity)
                throw new SecurityException(
                    "Protected staging path no longer names the leased directory identity.");
        }

        public void Publish(string destinationPath)
        {
            if (_published) throw new InvalidOperationException("Protected staging is already published.");
            var activeHandle = _handle
                ?? throw new ObjectDisposedException(nameof(WindowsProtectedDirectoryLease));
            Verify(CurrentPath);
            var destination = Path.TrimEndingDirectorySeparator(
                ReparsePointPolicy.EnsureNoTraversal(destinationPath, requireLeafExists: false));
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new IOException(
                    "Backup/restore destination already exists; overwrite is prohibited.");
            var destinationParent = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("Publish destination has no parent.");
            ReparsePointPolicy.EnsureNoTraversal(destinationParent, requireLeafExists: true);
            RenameByHandle(activeHandle, destination);
            if (!Directory.Exists(destination))
                throw new IOException(
                    "Handle-bound publish did not materialize the expected destination identity.");
            CurrentPath = destination;
            Verify(destination);
            _published = true;
        }

        public bool CleanupIfOwned()
        {
            if (_published || _handle is null) return false;
            try
            {
                Verify(CurrentPath);
                // Never recursively clean a failed backup/restore by name. If the
                // leased directory is not empty, FILE_DISPOSITION_INFO fails and
                // the protected partial is deliberately retained for inspection.
                // This is safer than traversing a tree whose children could have
                // been replaced after a path-based reparse check.
                MarkDeleteOnClose(_handle);
                _handle.Dispose();
                _handle = null;
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _handle?.Dispose();
            _handle = null;
        }

        private static void RenameByHandle(SafeFileHandle handle, string destination)
        {
            var fileNameBytes = checked(destination.Length * sizeof(char));
            var rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
            var fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
            var fileNameOffset = fileNameLengthOffset + sizeof(uint);
            var bufferBytes = checked(fileNameOffset + fileNameBytes + sizeof(char));
            var buffer = Marshal.AllocHGlobal(bufferBytes);
            try
            {
                var zeros = new byte[bufferBytes];
                Marshal.Copy(zeros, 0, buffer, bufferBytes);
                Marshal.WriteByte(buffer, 0, 0);
                Marshal.WriteIntPtr(buffer, rootDirectoryOffset, IntPtr.Zero);
                Marshal.WriteInt32(buffer, fileNameLengthOffset, fileNameBytes);
                var characters = destination.ToCharArray();
                Marshal.Copy(characters, 0, buffer + fileNameOffset, characters.Length);
                const int maximumAttempts = 6;
                for (var attempt = 1; attempt <= maximumAttempts; attempt++)
                {
                    if (NativeMethods.SetFileInformationByHandle(
                            handle, FileRenameInfo, buffer, checked((uint)bufferBytes)))
                        return;
                    var error = Marshal.GetLastPInvokeError();
                    if (attempt == maximumAttempts || Directory.Exists(destination) ||
                        File.Exists(destination))
                        throw new Win32Exception(
                            error,
                            $"Unable to publish the protected staging directory by identity (Win32 {error}).");
                    Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void MarkDeleteOnClose(SafeFileHandle handle)
        {
            var disposition = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(disposition, 1);
                if (!NativeMethods.SetFileInformationByHandle(
                        handle, FileDispositionInfo, disposition, 1))
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "Unable to clean the owned protected staging directory by identity.");
            }
            finally
            {
                Marshal.FreeHGlobal(disposition);
            }
        }
    }

    private readonly record struct DirectoryIdentity(uint VolumeSerialNumber, ulong FileIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

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
        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "CreateDirectoryW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CreateDirectory(
            string path,
            in SecurityAttributes securityAttributes);

        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            nint fileInformation,
            uint bufferSize);
    }
}
