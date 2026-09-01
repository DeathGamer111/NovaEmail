using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NovaEmail.Mail.Tests;

public sealed class CertificateTrustPolicyTests
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    [Fact]
    public void CustomTrustRequiresTlsServerAuthenticationPurpose()
    {
        using var serverCertificate = CreateSelfSignedCertificate(ServerAuthenticationOid);
        using var wrongPurposeCertificate = CreateSelfSignedCertificate(CodeSigningOid);

        Assert.True(new CertificateTrustPolicy([serverCertificate]).Validate(
            this, serverCertificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(new CertificateTrustPolicy([wrongPurposeCertificate]).Validate(
            this, wrongPurposeCertificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void CustomTrustNeverOverridesHostnameMismatchOrMissingCertificate()
    {
        using var certificate = CreateSelfSignedCertificate(ServerAuthenticationOid);
        var policy = new CertificateTrustPolicy([certificate]);

        Assert.False(policy.Validate(
            this, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(policy.Validate(
            this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string enhancedKeyUsageOid)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(enhancedKeyUsageOid) }, critical: true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pkcs12), password: null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
}
