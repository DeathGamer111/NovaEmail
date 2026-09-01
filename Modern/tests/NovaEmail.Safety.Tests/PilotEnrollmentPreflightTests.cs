namespace NovaEmail.Safety.Tests;

using System.Security.AccessControl;
using System.Security.Principal;

public sealed class PilotEnrollmentPreflightTests
{
    [Fact]
    public void EnrollmentFailsClosedWhenBitLockerCannotBeProven()
    {
        var preflight = new PilotEnrollmentPreflight(
            new FixedBitLockerProbe(false), new FixedAclProbe(true));
        var result = preflight.Inspect(AppContext.BaseDirectory);

        Assert.False(result.Passed);
        Assert.False(result.BitLocker.Passed);
        Assert.False(result.UnencryptedVolumeRiskAcceptance.Passed);
        Assert.False(result.BitLockerRiskExceptionApplied);
    }

    [Fact]
    public void EnrollmentAllowsExplicitTimeBoundedUnencryptedVolumeRiskAcceptance()
    {
        var preflight = new PilotEnrollmentPreflight(
            new FixedBitLockerProbe(false),
            new FixedAclProbe(true),
            new FixedRiskAcceptanceProbe(true));

        var result = preflight.Inspect(AppContext.BaseDirectory);

        Assert.True(result.Passed);
        Assert.True(result.BitLockerRiskExceptionApplied);
        Assert.True(result.UnencryptedVolumeRiskAcceptance.Passed);
    }

    [Fact]
    public void BitLockerPassesWithoutApplyingAStoredRiskException()
    {
        var preflight = new PilotEnrollmentPreflight(
            new FixedBitLockerProbe(true),
            new FixedAclProbe(true),
            new FixedRiskAcceptanceProbe(true));

        var result = preflight.Inspect(AppContext.BaseDirectory);

        Assert.True(result.Passed);
        Assert.False(result.BitLockerRiskExceptionApplied);
        Assert.False(result.UnencryptedVolumeRiskAcceptance.Passed);
    }

    [Fact]
    public async Task FileRiskAcceptanceRequiresStrictDevelopmentOnlyUnexpiredDocument()
    {
        var root = Path.Combine(
            FindRepositoryRoot(), ".artifacts", "risk-acceptance-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileUnencryptedVolumeRiskAcceptanceProbe.FileName);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(path, $$"""
                {
                  "SchemaVersion": 1,
                  "Accepted": true,
                  "Scope": "DevelopmentLocalStoreOnly",
                  "ApprovalId": "local-test-approval",
                  "Justification": "Explicit local test acceptance for an unencrypted development volume.",
                  "ApprovedUtc": "{{now:O}}",
                  "ExpiresUtc": "{{now.AddDays(7):O}}",
                  "ProductionDeploymentAuthorized": false
                }
                """);
            var probe = new FileUnencryptedVolumeRiskAcceptanceProbe();
            Assert.True(probe.Inspect(root).Passed);

            await File.WriteAllTextAsync(path, $$"""
                {
                  "SchemaVersion": 1,
                  "Accepted": true,
                  "Scope": "DevelopmentLocalStoreOnly",
                  "ApprovalId": "local-test-approval",
                  "Justification": "Expired local test acceptance for an unencrypted development volume.",
                  "ApprovedUtc": "{{now.AddDays(-2):O}}",
                  "ExpiresUtc": "{{now.AddDays(-1):O}}",
                  "ProductionDeploymentAuthorized": false
                }
                """);
            Assert.False(probe.Inspect(root).Passed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("ApprovalId", "null")]
    [InlineData("Justification", "null")]
    public async Task FileRiskAcceptanceRejectsNullRequiredStringsWithoutThrowing(
        string propertyName,
        string replacementJson)
    {
        var root = Path.Combine(
            FindRepositoryRoot(), ".artifacts", "risk-acceptance-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileUnencryptedVolumeRiskAcceptanceProbe.FileName);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var document = $$"""
                {
                  "SchemaVersion": 1,
                  "Accepted": true,
                  "Scope": "DevelopmentLocalStoreOnly",
                  "ApprovalId": "local-test-approval",
                  "Justification": "Explicit local test acceptance for an unencrypted development volume.",
                  "ApprovedUtc": "{{now:O}}",
                  "ExpiresUtc": "{{now.AddDays(7):O}}",
                  "ProductionDeploymentAuthorized": false
                }
                """;
            document = propertyName switch
            {
                "ApprovalId" => document.Replace(
                    "\"ApprovalId\": \"local-test-approval\"",
                    $"\"ApprovalId\": {replacementJson}",
                    StringComparison.Ordinal),
                "Justification" => document.Replace(
                    "\"Justification\": \"Explicit local test acceptance for an unencrypted development volume.\"",
                    $"\"Justification\": {replacementJson}",
                    StringComparison.Ordinal),
                _ => throw new ArgumentOutOfRangeException(nameof(propertyName)),
            };
            await File.WriteAllTextAsync(path, document);

            var result = new FileUnencryptedVolumeRiskAcceptanceProbe().Inspect(root);

            Assert.False(result.Passed);
            Assert.Contains("invalid", result.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FileRiskAcceptanceRejectsDuplicateJsonProperties()
    {
        var root = Path.Combine(
            FindRepositoryRoot(), ".artifacts", "risk-acceptance-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileUnencryptedVolumeRiskAcceptanceProbe.FileName);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(path, $$"""
                {
                  "SchemaVersion": 1,
                  "Accepted": false,
                  "Accepted": true,
                  "Scope": "DevelopmentLocalStoreOnly",
                  "ApprovalId": "duplicate-property-test",
                  "Justification": "A duplicate approval field must never be resolved by last-value-wins parsing.",
                  "ApprovedUtc": "{{now:O}}",
                  "ExpiresUtc": "{{now.AddDays(7):O}}",
                  "ProductionDeploymentAuthorized": false
                }
                """);

            var result = new FileUnencryptedVolumeRiskAcceptanceProbe().Inspect(root);

            Assert.False(result.Passed);
            Assert.Contains("ambiguous", result.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CredentialVaultRejectsKeysOutsideItsConstrainedDevelopmentNamespace()
    {
        var vault = new WindowsCredentialVault();
        Assert.Throws<ArgumentException>(() => vault.Read("../invalid-account"));
        Assert.Throws<ArgumentException>(() => vault.Delete("production/account"));
    }

    [Fact]
    public void CredentialVaultRejectsMalformedValuesBeforeNativePersistence()
    {
        var vault = new WindowsCredentialVault();
        const string key = "validation-only";

        Assert.Throws<ArgumentException>(() =>
            vault.Store(key, new StoredMailCredential(string.Empty, "password")));
        Assert.Throws<ArgumentException>(() =>
            vault.Store(key, new StoredMailCredential("user\r\nname", "password")));
        Assert.Throws<ArgumentException>(() =>
            vault.Store(key, new StoredMailCredential("user", string.Empty)));
        Assert.Throws<ArgumentException>(() =>
            vault.Store(key, new StoredMailCredential("user", "   ")));
        Assert.Throws<ArgumentException>(() =>
            vault.Store(key, new StoredMailCredential("user", "before\0after")));
        Assert.Throws<ArgumentException>(() =>
            vault.Store(key, new StoredMailCredential("user", new string('x', 257))));
    }

    [Fact]
    public void CredentialValueFormattingNeverIncludesUserNameOrPassword()
    {
        var credential = new StoredMailCredential(
            "format-user-" + Guid.NewGuid().ToString("N"),
            "format-password-" + Guid.NewGuid().ToString("N"));

        var formatted = credential.ToString();

        Assert.DoesNotContain(credential.UserName, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.Password, formatted, StringComparison.Ordinal);
        Assert.Contains("redacted", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CredentialVaultRoundTripsOnlyDisposableDevelopmentCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vault = new WindowsCredentialVault();
        var accountKey = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            vault.Store(accountKey, new StoredMailCredential("local-lab-user", "disposable-local-secret"));
            Assert.Equal(
                new StoredMailCredential("local-lab-user", "disposable-local-secret"),
                vault.Read(accountKey));
        }
        finally
        {
            vault.Delete(accountKey);
        }
        Assert.Null(vault.Read(accountKey));
    }

    [Fact]
    public void DevelopmentSecretVaultRejectsMalformedKeysAndSecretsBeforePersistence()
    {
        var vault = new WindowsSecretVault();
        Assert.Throws<ArgumentException>(() => vault.Read("../mail-credential"));
        Assert.Throws<ArgumentException>(() => vault.Delete("assistant/key"));
        Assert.Throws<ArgumentException>(() =>
            vault.Store("validation-only", new StoredSecret(string.Empty)));
        Assert.Throws<ArgumentException>(() =>
            vault.Store("validation-only", new StoredSecret("contains space")));
        Assert.Throws<ArgumentException>(() =>
            vault.Store("validation-only", new StoredSecret("line\nbreak")));
        Assert.Throws<ArgumentException>(() =>
            vault.Store("validation-only", new StoredSecret(new string('x', 257))));
    }

    [Fact]
    public void DevelopmentSecretFormattingIsAlwaysRedacted()
    {
        var secret = new StoredSecret("secret-" + Guid.NewGuid().ToString("N"));

        var formatted = secret.ToString();

        Assert.DoesNotContain(secret.Secret, formatted, StringComparison.Ordinal);
        Assert.Contains("redacted", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DevelopmentSecretVaultRoundTripsOnlyDisposableAssistantSecrets()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vault = new WindowsSecretVault();
        var secretKey = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            vault.Store(secretKey, new StoredSecret("disposable-local-api-key"));
            Assert.Equal(
                new StoredSecret("disposable-local-api-key"),
                vault.Read(secretKey));
        }
        finally
        {
            vault.Delete(secretKey);
        }
        Assert.Null(vault.Read(secretKey));
    }

    [Fact]
    public async Task CurrentUserAclProbeRequiresProtectedDaclAndFullControl()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(
            FindRepositoryRoot(), ".artifacts", "acl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var probe = new CurrentUserOnlyAclProbe();
            Assert.False(probe.Inspect(root).Passed);
            Assert.False(CurrentUserOnlyAclProbe.InspectRootOnly(root).Passed);

            var currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Current SID unavailable in ACL test.");
            new DirectoryInfo(root).SetAccessControl(CreateCurrentUserOnlyDacl(currentUser));

            Assert.True(probe.Inspect(root).Passed);
            Assert.True(CurrentUserOnlyAclProbe.InspectRootOnly(root).Passed);

            var child = Path.Combine(root, "nested");
            Directory.CreateDirectory(child);
            await File.WriteAllTextAsync(Path.Combine(child, "message.eml"), "fixture");
            Assert.True(probe.Inspect(root).Passed);

            var childSecurity = CreateCurrentUserOnlyDacl(currentUser);
            childSecurity.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadData,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(child).SetAccessControl(childSecurity);
            var childResult = probe.Inspect(root);
            Assert.False(childResult.Passed);
            Assert.Contains("additional principal", childResult.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.True(CurrentUserOnlyAclProbe.InspectRootOnly(root).Passed);

            new DirectoryInfo(child).SetAccessControl(CreateCurrentUserOnlyDacl(currentUser));

            var denySecurity = CreateCurrentUserOnlyDacl(currentUser);
            denySecurity.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadData,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Deny));
            new DirectoryInfo(root).SetAccessControl(denySecurity);
            try
            {
                var result = probe.Inspect(root);
                Assert.False(result.Passed);
                Assert.Contains("deny ACL", result.Detail, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                new DirectoryInfo(root).SetAccessControl(CreateCurrentUserOnlyDacl(currentUser));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedBitLockerProbe(bool passed) : IBitLockerProtectionProbe
    {
        public ProtectionProbeResult Inspect(string canonicalDataRoot) => new(passed, "test result");
    }

    private sealed class FixedAclProbe(bool passed) : ICurrentUserAclProbe
    {
        public ProtectionProbeResult Inspect(string canonicalDataRoot) => new(passed, "test result");
    }

    private sealed class FixedRiskAcceptanceProbe(bool passed) : IUnencryptedVolumeRiskAcceptanceProbe
    {
        public ProtectionProbeResult Inspect(string canonicalDataRoot) => new(passed, "test result");
    }

    private static DirectorySecurity CreateCurrentUserOnlyDacl(SecurityIdentifier currentUser)
    {
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
        {
            currentUser,
            new SecurityIdentifier("S-1-5-18"),
            new SecurityIdentifier("S-1-5-32-544"),
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Modern")) &&
                Directory.Exists(Path.Combine(directory.FullName, "TestLab"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
