using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using NovaEmail.Safety;
using NovaEmail.Storage;

namespace NovaEmail.Storage.Tests;

public sealed class ProtectedDirectoryBoundaryTests
{
    [Fact]
    public async Task ExclusiveProtectedDirectoryPublishesTheExactLeasedIdentity()
    {
        var testRoot = CreateTestRoot();
        var stagingPath = Path.Combine(testRoot, "backup.partial.synthetic");
        var destinationPath = Path.Combine(testRoot, "backup");
        try
        {
            using var lease = WindowsProtectedDirectoryBoundary.Instance
                .CreateExclusive(stagingPath);
            var payload = Path.Combine(stagingPath, "payload.bin");
            await File.WriteAllBytesAsync(payload, [1, 2, 3, 4]);

            lease.Verify(stagingPath);
            lease.Publish(destinationPath);

            Assert.Equal(destinationPath, lease.CurrentPath);
            Assert.False(Directory.Exists(stagingPath));
            Assert.Equal(new byte[] { 1, 2, 3, 4 },
                await File.ReadAllBytesAsync(Path.Combine(destinationPath, "payload.bin")));
            Assert.True(new DirectoryInfo(destinationPath)
                .GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected);
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PublishRejectsPrecreatedDestinationWithoutChangingEitherDirectory()
    {
        var testRoot = CreateTestRoot();
        var stagingPath = Path.Combine(testRoot, "backup.partial.collision");
        var destinationPath = Path.Combine(testRoot, "backup");
        try
        {
            using var lease = WindowsProtectedDirectoryBoundary.Instance
                .CreateExclusive(stagingPath);
            var stagingPayload = Path.Combine(stagingPath, "payload.bin");
            await File.WriteAllBytesAsync(stagingPayload, [1, 2, 3, 4]);
            Directory.CreateDirectory(destinationPath);
            var destinationSentinel = Path.Combine(destinationPath, "preserve.txt");
            await File.WriteAllTextAsync(destinationSentinel, "preserve");

            var exception = Assert.Throws<IOException>(() => lease.Publish(destinationPath));

            Assert.Contains("overwrite", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(stagingPath, lease.CurrentPath);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(stagingPayload));
            Assert.Equal("preserve", await File.ReadAllTextAsync(destinationSentinel));
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExclusiveCreationRejectsPrecreatedLeafWithoutChangingItsSentinel()
    {
        var testRoot = CreateTestRoot();
        var stagingPath = Path.Combine(testRoot, "backup.partial.precreated");
        Directory.CreateDirectory(stagingPath);
        var sentinelPath = Path.Combine(stagingPath, "sentinel.bin");
        await File.WriteAllBytesAsync(sentinelPath, "replacement sentinel"u8.ToArray());
        var hashBefore = SHA256.HashData(await File.ReadAllBytesAsync(sentinelPath));
        try
        {
            var exception = Assert.Throws<IOException>(() =>
                WindowsProtectedDirectoryBoundary.Instance.CreateExclusive(stagingPath));

            Assert.DoesNotContain(stagingPath, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sentinel", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(hashBefore, SHA256.HashData(await File.ReadAllBytesAsync(sentinelPath)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void EmptyOwnedPartialIsDeletedOnlyThroughItsLeasedIdentityHandle()
    {
        var testRoot = CreateTestRoot();
        var stagingPath = Path.Combine(testRoot, "empty.partial.synthetic");
        try
        {
            using var lease = WindowsProtectedDirectoryBoundary.Instance
                .CreateExclusive(stagingPath);

            Assert.True(lease.CleanupIfOwned());
            Assert.False(Directory.Exists(stagingPath));
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExclusiveLeasePreventsPathReplacementAndRetainsNonEmptyPartial()
    {
        var testRoot = CreateTestRoot();
        var stagingPath = Path.Combine(testRoot, "restore.partial.swap");
        var displacedPath = Path.Combine(testRoot, "displaced-owned-staging");
        try
        {
            using var lease = WindowsProtectedDirectoryBoundary.Instance
                .CreateExclusive(stagingPath);
            await File.WriteAllTextAsync(Path.Combine(stagingPath, "owned.txt"), "owned");

            Assert.ThrowsAny<IOException>(() => Directory.Move(stagingPath, displacedPath));
            lease.Verify(stagingPath);
            Assert.False(lease.CleanupIfOwned());
            Assert.False(Directory.Exists(displacedPath));
            Assert.Equal("owned", await File.ReadAllTextAsync(Path.Combine(stagingPath, "owned.txt")));
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BackupServiceBlocksAPathSwapImmediatelyAfterItsInitialLeaseVerification()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            var destination = Path.Combine(testRoot, "backup");
            var boundary = new RenameAttemptProtectedDirectoryBoundary();

            var result = await ModernMailBackupService.CreateAsync(store, destination, boundary);

            Assert.True(boundary.RenameAttempted);
            Assert.True(boundary.RenameBlocked);
            Assert.False(boundary.RenameSucceeded);
            Assert.False(Directory.Exists(boundary.DisplacedPath));
            Assert.Equal(destination, result.BackupRoot);
            Assert.True(File.Exists(Path.Combine(destination, "backup-manifest.json")));
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProtectedBackupAndRestoreExplicitlyInheritTheSelectedParentsRiskAcceptance()
    {
        if (!OperatingSystem.IsWindows()) return;
        var testRoot = CreateTestRoot();
        try
        {
            var storeRoot = Path.Combine(testRoot, "protected-store");
            CreateCurrentUserOnlyDirectory(storeRoot);
            _ = await WriteRiskAcceptanceAsync(storeRoot, "store-approval");
            var store = new ModernMailStore(Path.Combine(storeRoot, "novaemail.db"), storeRoot);
            await store.InitializeAsync();

            var backupParent = Path.Combine(testRoot, "protected-backup-parent");
            CreateCurrentUserOnlyDirectory(backupParent);
            var backupAcceptance = await WriteRiskAcceptanceAsync(
                backupParent, "backup-approval");
            var backupRoot = Path.Combine(backupParent, "backup");
            var backupDestination = ManagedWriteDestinationPolicy
                .CreateDevelopment([storeRoot])
                .ApproveNewRoot(backupRoot);
            var bitLockerUnavailable = new FixedBitLockerProbe(false);

            var backup = await ModernMailBackupService.CreateProtectedAsync(
                store, backupDestination, bitLockerUnavailable);

            Assert.Equal(
                backupAcceptance,
                await File.ReadAllTextAsync(Path.Combine(
                    backup.BackupRoot, FileUnencryptedVolumeRiskAcceptanceProbe.FileName)));
            Assert.True(new PilotEnrollmentPreflight(
                bitLockerUnavailable, new CurrentUserOnlyAclProbe()).Inspect(backup.BackupRoot).Passed);

            var restoreParent = Path.Combine(testRoot, "protected-restore-parent");
            CreateCurrentUserOnlyDirectory(restoreParent);
            var restoreAcceptance = await WriteRiskAcceptanceAsync(
                restoreParent, "restore-approval");
            var restoreRoot = Path.Combine(restoreParent, "restore");
            var restoreDestination = ManagedWriteDestinationPolicy
                .CreateDevelopment([storeRoot, backup.BackupRoot])
                .ApproveNewRoot(restoreRoot);

            var restored = await ModernMailBackupService.RestoreProtectedAsync(
                backup.BackupRoot, restoreDestination, bitLockerUnavailable);

            var restoredAcceptance = await File.ReadAllTextAsync(Path.Combine(
                restored.RestoredDataRoot, FileUnencryptedVolumeRiskAcceptanceProbe.FileName));
            Assert.Equal(restoreAcceptance, restoredAcceptance);
            Assert.NotEqual(backupAcceptance, restoredAcceptance);
            Assert.True(new PilotEnrollmentPreflight(
                bitLockerUnavailable, new CurrentUserOnlyAclProbe())
                .Inspect(restored.RestoredDataRoot).Passed);
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ServiceCleanupSeamDoesNotRecursivelyDeleteSimulatedReplacement()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "data");
            var store = new ModernMailStore(Path.Combine(dataRoot, "novaemail.db"), dataRoot);
            await store.InitializeAsync();
            var sourceHashBefore = await HashTreeAsync(dataRoot);
            var destination = Path.Combine(testRoot, "backup");
            var boundary = new ReplacingProtectedDirectoryBoundary();

            await Assert.ThrowsAsync<SecurityException>(() => ModernMailBackupService.CreateAsync(
                store, destination, boundary));

            Assert.True(boundary.CleanupAttempted);
            Assert.False(Directory.Exists(destination));
            Assert.NotNull(boundary.ReplacementSentinelPath);
            Assert.Equal("replacement must survive", await File.ReadAllTextAsync(
                boundary.ReplacementSentinelPath));
            Assert.Equal(sourceHashBefore, await HashTreeAsync(dataRoot));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(
            FindRepositoryRoot(), ".artifacts", "protected-directory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CreateCurrentUserOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current SID unavailable in protected storage test.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static async Task<string> WriteRiskAcceptanceAsync(string root, string approvalId)
    {
        var now = DateTimeOffset.UtcNow;
        var document = $$"""
            {
              "SchemaVersion": 1,
              "Accepted": true,
              "Scope": "DevelopmentLocalStoreOnly",
              "ApprovalId": "{{approvalId}}",
              "Justification": "Explicit synthetic test acceptance for a local unencrypted development volume.",
              "ApprovedUtc": "{{now:O}}",
              "ExpiresUtc": "{{now.AddDays(7):O}}",
              "ProductionDeploymentAuthorized": false
            }
            """;
        await File.WriteAllTextAsync(
            Path.Combine(root, FileUnencryptedVolumeRiskAcceptanceProbe.FileName), document);
        return document;
    }

    private static async Task<IReadOnlyDictionary<string, string>> HashTreeAsync(string root)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) &&
                         !path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            hashes[Path.GetRelativePath(root, path)] = Convert.ToHexStringLower(
                SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }
        return hashes;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "NovaClient")) &&
                Directory.Exists(Path.Combine(directory.FullName, "TestLab")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private sealed class ReplacingProtectedDirectoryBoundary : IProtectedDirectoryBoundary
    {
        public bool CleanupAttempted { get; private set; }
        public string? ReplacementSentinelPath { get; private set; }

        public IProtectedDirectoryLease CreateExclusive(string path)
        {
            Directory.CreateDirectory(path);
            return new ReplacingLease(this, path);
        }

        private sealed class ReplacingLease(
            ReplacingProtectedDirectoryBoundary owner,
            string currentPath) : IProtectedDirectoryLease
        {
            private bool _replaced;

            public string CurrentPath { get; private set; } = currentPath;

            public void Verify(string expectedPath)
            {
                if (_replaced ||
                    !string.Equals(expectedPath, CurrentPath, StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException("Simulated protected-directory identity mismatch.");
            }

            public void Publish(string destinationPath)
            {
                Verify(CurrentPath);
                var displaced = CurrentPath + ".displaced";
                Directory.Move(CurrentPath, displaced);
                Directory.CreateDirectory(CurrentPath);
                owner.ReplacementSentinelPath = Path.Combine(CurrentPath, "replacement.txt");
                File.WriteAllText(
                    owner.ReplacementSentinelPath, "replacement must survive", new UTF8Encoding(false));
                _replaced = true;
                throw new SecurityException("Simulated replacement detected before publish.");
            }

            public bool CleanupIfOwned()
            {
                owner.CleanupAttempted = true;
                return false;
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class FixedBitLockerProbe(bool passed) : IBitLockerProtectionProbe
    {
        public ProtectionProbeResult Inspect(string canonicalDataRoot) =>
            new(passed, "Synthetic protected-storage test result.");
    }

    private sealed class RenameAttemptProtectedDirectoryBoundary : IProtectedDirectoryBoundary
    {
        public bool RenameAttempted { get; private set; }
        public bool RenameBlocked { get; private set; }
        public bool RenameSucceeded { get; private set; }
        public string DisplacedPath { get; private set; } = string.Empty;

        public IProtectedDirectoryLease CreateExclusive(string path)
        {
            var inner = WindowsProtectedDirectoryBoundary.Instance.CreateExclusive(path);
            DisplacedPath = path + ".displaced";
            return new RenameAttemptLease(this, inner);
        }

        private sealed class RenameAttemptLease(
            RenameAttemptProtectedDirectoryBoundary owner,
            IProtectedDirectoryLease inner) : IProtectedDirectoryLease
        {
            public string CurrentPath => inner.CurrentPath;

            public void Verify(string expectedPath)
            {
                inner.Verify(expectedPath);
                if (owner.RenameAttempted) return;
                owner.RenameAttempted = true;
                try
                {
                    Directory.Move(inner.CurrentPath, owner.DisplacedPath);
                    owner.RenameSucceeded = true;
                }
                catch (IOException)
                {
                    owner.RenameBlocked = true;
                }
                catch (UnauthorizedAccessException)
                {
                    owner.RenameBlocked = true;
                }
            }

            public void Publish(string destinationPath) => inner.Publish(destinationPath);

            public bool CleanupIfOwned() => inner.CleanupIfOwned();

            public void Dispose() => inner.Dispose();
        }
    }
}
