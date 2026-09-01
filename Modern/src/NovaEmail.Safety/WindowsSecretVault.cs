using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NovaEmail.Safety;

public sealed record StoredSecret(string Secret)
{
    public override string ToString() => "StoredSecret { <redacted> }";
}

public interface ISecretVault
{
    void Store(string secretKey, StoredSecret secret);
    StoredSecret? Read(string secretKey);
    bool Delete(string secretKey);
}

/// <summary>
/// Stores only explicitly enrolled development-assistant secrets in a dedicated
/// Windows Credential Manager namespace separate from mail credentials.
/// </summary>
public sealed partial class WindowsSecretVault : ISecretVault
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumSecretCharacters = 256;
    private const string TargetPrefix = "NovaEmail/Assistant/";

    public void Store(string secretKey, StoredSecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var target = BuildTarget(secretKey);
        ValidateSecret(secret);
        EnsureWindows();
        nint targetPointer = 0;
        nint secretPointer = 0;
        try
        {
            targetPointer = Marshal.StringToCoTaskMemUni(target);
            secretPointer = Marshal.StringToCoTaskMemUni(secret.Secret);
            var native = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetPointer,
                CredentialBlobSize = checked((uint)(secret.Secret.Length * sizeof(char))),
                CredentialBlob = secretPointer,
                Persist = PersistLocalMachine,
            };
            if (!NativeMethods.CredWrite(ref native, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (secretPointer != 0) Marshal.ZeroFreeCoTaskMemUnicode(secretPointer);
            if (targetPointer != 0) Marshal.FreeCoTaskMem(targetPointer);
        }
    }

    public StoredSecret? Read(string secretKey)
    {
        EnsureWindows();
        var target = BuildTarget(secretKey);
        if (!NativeMethods.CredRead(target, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize is 0 ||
                credential.CredentialBlobSize > checked((uint)(MaximumSecretCharacters * sizeof(char))) ||
                credential.CredentialBlobSize % sizeof(char) != 0 || credential.CredentialBlob == 0)
                throw new InvalidDataException("Stored development secret has an invalid blob length.");
            var value = Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / sizeof(char))) ?? string.Empty;
            var secret = new StoredSecret(value);
            try
            {
                ValidateSecret(secret);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "Stored secret is outside the development assistant safety boundary.", exception);
            }
            return secret;
        }
        finally
        {
            NativeMethods.CredFree(credentialPointer);
        }
    }

    public bool Delete(string secretKey)
    {
        EnsureWindows();
        var target = BuildTarget(secretKey);
        if (NativeMethods.CredDelete(target, GenericCredential, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw new Win32Exception(error);
    }

    private static string BuildTarget(string secretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        if (secretKey.Length > 128 || secretKey.Any(character =>
            !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException(
                "Secret key contains characters prohibited in the development assistant namespace.",
                nameof(secretKey));
        return TargetPrefix + secretKey;
    }

    private static void ValidateSecret(StoredSecret secret)
    {
        if (string.IsNullOrWhiteSpace(secret.Secret) ||
            secret.Secret.Length > MaximumSecretCharacters ||
            secret.Secret.Any(char.IsControl) || secret.Secret.Any(char.IsWhiteSpace))
            throw new ArgumentException(
                "Development secret is empty, malformed, or exceeds its limit.", nameof(secret));
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Credential Manager is required.");
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

        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredRead(string target, uint type, uint flags, out nint credential);

        [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredDelete(string target, uint type, uint flags);

        [LibraryImport("advapi32.dll")]
        internal static partial void CredFree(nint buffer);
    }
}
