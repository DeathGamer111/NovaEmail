using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NovaEmail.Mail;

public sealed class CertificateTrustPolicy
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private readonly X509Certificate2Collection _customTrustRoots;

    public CertificateTrustPolicy(IEnumerable<X509Certificate2>? customTrustRoots = null)
    {
        _customTrustRoots = new X509Certificate2Collection();
        if (customTrustRoots is not null)
        {
            _customTrustRoots.AddRange(customTrustRoots.ToArray());
        }
    }

    public bool Validate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is null ||
            errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ||
            errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return false;
        }

        if (_customTrustRoots.Count == 0)
        {
            return errors == SslPolicyErrors.None;
        }

        using var leaf = certificate as X509Certificate2 ??
            X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var customChain = new X509Chain();
        customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        customChain.ChainPolicy.CustomTrustStore.AddRange(_customTrustRoots);
        customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        customChain.ChainPolicy.DisableCertificateDownloads = true;
        customChain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        customChain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthenticationOid));
        return customChain.Build(leaf);
    }
}
