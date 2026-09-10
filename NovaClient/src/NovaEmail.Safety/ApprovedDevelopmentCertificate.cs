using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NovaEmail.Safety;

public static class ApprovedDevelopmentCertificate
{
    private const int MaximumCertificateBytes = 1024 * 1024;

    public static X509Certificate2 LoadCertificateAuthority(
        string certificatePath,
        string expectedSha256,
        string approvedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedRoot);
        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new SecurityException("Approved development CA SHA-256 is invalid.");

        var canonicalRoot = Path.GetFullPath(approvedRoot).TrimEnd(Path.DirectorySeparatorChar);
        var canonicalPath = Path.GetFullPath(certificatePath);
        ReparsePointPolicy.EnsureNoTraversal(canonicalRoot, requireLeafExists: true);
        var rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!canonicalPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Development CA escaped its approved local root.");
        if (!string.Equals(Path.GetExtension(canonicalPath), ".cer", StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Development CA must be an unencrypted public .cer file.");
        EnsureNoReparsePoints(canonicalRoot, canonicalPath);

        using var input = new FileStream(
            canonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.SequentialScan);
        if (input.Length is <= 0 or > MaximumCertificateBytes)
            throw new SecurityException("Development CA file size is invalid.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Development CA does not match its approved SHA-256.");

        var certificate = X509CertificateLoader.LoadCertificate(bytes);
        try
        {
            if (certificate.HasPrivateKey)
                throw new SecurityException("Development trust input unexpectedly contains a private key.");
            var basicConstraints = certificate.Extensions
                .OfType<X509BasicConstraintsExtension>()
                .SingleOrDefault();
            if (basicConstraints?.CertificateAuthority is not true)
                throw new SecurityException("Development trust input is not a certificate authority.");
            var now = DateTimeOffset.UtcNow;
            if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
                throw new SecurityException("Development certificate authority is not currently valid.");
            using var rsa = certificate.GetRSAPublicKey();
            using var ecdsa = certificate.GetECDsaPublicKey();
            if (rsa is not null && rsa.KeySize < 2048 || ecdsa is not null && ecdsa.KeySize < 256 ||
                rsa is null && ecdsa is null)
                throw new SecurityException("Development certificate authority uses an unsupported public key.");
            return certificate;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    private static void EnsureNoReparsePoints(string canonicalRoot, string canonicalPath)
    {
        if (!Directory.Exists(canonicalRoot))
            throw new DirectoryNotFoundException("Approved development CA root does not exist.");
        if (!File.Exists(canonicalPath))
            throw new FileNotFoundException("Approved development CA does not exist.", canonicalPath);
        var current = new DirectoryInfo(Path.GetDirectoryName(canonicalPath)!);
        while (true)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SecurityException("Development CA path cannot traverse a reparse point.");
            if (current.FullName.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)) break;
            current = current.Parent ??
                throw new SecurityException("Development CA path escaped its approved root.");
        }
        if ((File.GetAttributes(canonicalPath) & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException("Development CA file cannot be a reparse point.");
    }
}
