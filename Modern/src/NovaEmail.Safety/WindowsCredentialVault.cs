using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NovaEmail.Safety;

public sealed record StoredMailCredential(string UserName, string Password)
{
    public override string ToString() => "StoredMailCredential { <redacted> }";
}

public interface ICredentialVault
{
    void Store(string accountKey, StoredMailCredential credential);
    StoredMailCredential? Read(string accountKey);
    bool Delete(string accountKey);
}

public sealed partial class WindowsCredentialVault : ICredentialVault
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumUserNameCharacters = 513;
    private const int MaximumPasswordCharacters = 256;
    private const string TargetPrefix = "NovaEmail/Mail/";

    public void Store(string accountKey, StoredMailCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var target = BuildTarget(accountKey);
        ValidateCredential(credential);
        EnsureWindows();
        nint targetPointer = 0;
        nint userPointer = 0;
        nint passwordPointer = 0;
        try
        {
            targetPointer = Marshal.StringToCoTaskMemUni(target);
            userPointer = Marshal.StringToCoTaskMemUni(credential.UserName);
            passwordPointer = Marshal.StringToCoTaskMemUni(credential.Password);
            var native = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetPointer,
                CredentialBlobSize = checked((uint)(credential.Password.Length * sizeof(char))),
                CredentialBlob = passwordPointer,
                Persist = PersistLocalMachine,
                UserName = userPointer,
            };
            if (!NativeMethods.CredWrite(ref native, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (passwordPointer != 0) Marshal.ZeroFreeCoTaskMemUnicode(passwordPointer);
            if (userPointer != 0) Marshal.ZeroFreeCoTaskMemUnicode(userPointer);
            if (targetPointer != 0) Marshal.FreeCoTaskMem(targetPointer);
        }
    }

    public StoredMailCredential? Read(string accountKey)
    {
        EnsureWindows();
        var target = BuildTarget(accountKey);
        if (!NativeMethods.CredRead(target, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize > checked((uint)(MaximumPasswordCharacters * sizeof(char))) ||
                credential.CredentialBlobSize % sizeof(char) != 0 ||
                (credential.CredentialBlobSize > 0 && credential.CredentialBlob == 0))
                throw new InvalidDataException("Stored credential has an invalid blob length.");
            var password = credential.CredentialBlob == 0
                ? string.Empty
                : Marshal.PtrToStringUni(
                    credential.CredentialBlob, checked((int)credential.CredentialBlobSize / sizeof(char))) ?? string.Empty;
            var userName = credential.UserName == 0 ? string.Empty : Marshal.PtrToStringUni(credential.UserName) ?? string.Empty;
            var stored = new StoredMailCredential(userName, password);
            try
            {
                ValidateCredential(stored);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "Stored credential is outside the development credential safety boundary.", exception);
            }
            return stored;
        }
        finally
        {
            NativeMethods.CredFree(credentialPointer);
        }
    }

    public bool Delete(string accountKey)
    {
        EnsureWindows();
        var target = BuildTarget(accountKey);
        if (NativeMethods.CredDelete(target, GenericCredential, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw new Win32Exception(error);
    }

    private static string BuildTarget(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        if (accountKey.Length > 128 || accountKey.Any(character =>
            !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException("Account key contains characters prohibited in the development credential namespace.", nameof(accountKey));
        return TargetPrefix + accountKey;
    }

    private static void ValidateCredential(StoredMailCredential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.UserName) ||
            credential.UserName.Length > MaximumUserNameCharacters ||
            credential.UserName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Credential user name is empty, contains control characters, or exceeds its limit.",
                nameof(credential));
        }
        if (string.IsNullOrWhiteSpace(credential.Password) ||
            credential.Password.Length > MaximumPasswordCharacters ||
            credential.Password.Contains('\0'))
        {
            throw new ArgumentException(
                "Credential password is empty, contains NUL, or exceeds its limit.",
                nameof(credential));
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Credential Manager is required.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public uint LastWrittenLow;
        public int LastWrittenHigh;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredWrite(ref NativeCredential credential, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredRead(string target, uint type, uint flags, out nint credential);

        [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredDelete(string target, uint type, uint flags);

        [LibraryImport("advapi32.dll")]
        internal static partial void CredFree(nint buffer);
    }
}
