using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NovaEmail.Safety.Tests;

public sealed class ApprovedDevelopmentCertificateTests
{
    [Fact]
    public void HashPinnedPublicCertificateAuthorityLoadsWithoutInstallation()
    {
        var root = CreateTestRoot();
        try
        {
            var (path, hash) = WriteCertificate(root, certificateAuthority: true);
            using var loaded = ApprovedDevelopmentCertificate.LoadCertificateAuthority(path, hash, root);

            Assert.False(loaded.HasPrivateKey);
            Assert.True(loaded.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HashMismatchAndNonCaInputAreRejected()
    {
        var root = CreateTestRoot();
        try
        {
            var (caPath, _) = WriteCertificate(root, certificateAuthority: true, "ca.cer");
            var mismatch = new string('0', 64);
            Assert.Throws<SecurityException>(() =>
                ApprovedDevelopmentCertificate.LoadCertificateAuthority(caPath, mismatch, root));

            var (leafPath, leafHash) = WriteCertificate(root, certificateAuthority: false, "leaf.cer");
            Assert.Throws<SecurityException>(() =>
                ApprovedDevelopmentCertificate.LoadCertificateAuthority(leafPath, leafHash, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CertificateOutsideApprovedRootIsRejected()
    {
        var root = CreateTestRoot();
        var other = CreateTestRoot();
        try
        {
            var (path, hash) = WriteCertificate(other, certificateAuthority: true);
            Assert.Throws<SecurityException>(() =>
                ApprovedDevelopmentCertificate.LoadCertificateAuthority(path, hash, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }

    private static (string Path, string Hash) WriteCertificate(
        string root,
        bool certificateAuthority,
        string fileName = "test-ca.cer")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            certificateAuthority ? "CN=NovaEmail Test CA" : "CN=localhost",
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            certificateAuthority ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature,
            true));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var bytes = certificate.Export(X509ContentType.Cert);
        var path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, bytes);
        return (path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static string CreateTestRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "NovaClient")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException();
        var root = Path.Combine(
            directory.FullName, ".artifacts", "approved-ca-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
