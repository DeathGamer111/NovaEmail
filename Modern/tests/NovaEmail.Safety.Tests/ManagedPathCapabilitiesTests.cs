using System.Security.Cryptography;
using NovaEmail.Safety;

namespace NovaEmail.Safety.Tests;

public sealed class ManagedPathCapabilitiesTests
{
    private static readonly string[] ProgramFilesShortNames = ["PROGRA~1", "PROGRA~2"];

    [Fact]
    public async Task RejectsNonexistentDescendantAndLeavesProtectedSourceByteIdentical()
    {
        var testRoot = CreateTestRoot();
        var sourceRoot = Path.Combine(testRoot, "protected-source-copy");
        Directory.CreateDirectory(sourceRoot);
        var sourceFile = Path.Combine(sourceRoot, "store.db");
        await File.WriteAllBytesAsync(sourceFile, "immutable protected source"u8.ToArray());
        var hashBefore = SHA256.HashData(await File.ReadAllBytesAsync(sourceFile));
        var destination = Path.Combine(sourceRoot, "not-created", "backup");
        try
        {
            var policy = ManagedWriteDestinationPolicy.CreateDevelopment([sourceRoot]);

            Assert.Throws<SecurityException>(() => policy.ApproveNewRoot(destination));

            Assert.False(Directory.Exists(Path.Combine(sourceRoot, "not-created")));
            Assert.Equal(hashBefore, SHA256.HashData(await File.ReadAllBytesAsync(sourceFile)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void ApprovesOnlyANewLocalSiblingWithoutCreatingIt()
    {
        var testRoot = CreateTestRoot();
        var sourceRoot = Path.Combine(testRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        var destination = Path.Combine(testRoot, "new-destination");
        try
        {
            var approved = ManagedWriteDestinationPolicy
                .CreateDevelopment([sourceRoot])
                .ApproveNewRoot(destination);

            Assert.Equal(Path.GetFullPath(destination), approved.ApprovedDataRoot);
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void RejectsNonexistentInstallRootDescendantBeforeAnyWrite()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrWhiteSpace(programFiles)) return;
        var destination = Path.Combine(
            programFiles, "NovaEmail", $"not-created-{Guid.NewGuid():N}", "backup");

        Assert.Throws<SecurityException>(() =>
            ManagedWriteDestinationPolicy.CreateDevelopment().ApproveNewRoot(destination));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void RejectsEveryDefaultApplicationRootIncludingMissingDescendants()
    {
        var roots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaEmail"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaEmail"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NovaEmail"),
            Path.Combine(ApplicationIdentity.FixtureRoot, "Masters"),
            ApplicationIdentity.RunRoot,
        }.Where(root => !string.IsNullOrWhiteSpace(Path.GetDirectoryName(root)));
        var policy = ManagedWriteDestinationPolicy.CreateDevelopment();

        foreach (var root in roots)
        {
            var destination = Path.Combine(root, $"not-created-{Guid.NewGuid():N}", "output");
            Assert.Throws<SecurityException>(() => policy.ApproveNewRoot(destination));
            Assert.False(Directory.Exists(destination));
        }
    }

    [Theory]
    [InlineData(@"\\?\C:\NovaEmail\output")]
    [InlineData(@"\\.\C:\NovaEmail\output")]
    [InlineData(@"\\localhost\NovaEmail\output")]
    [InlineData(@"C:\NovaEmail\output.json:alternate")]
    [InlineData(@"C:\NovaEmail\NUL\output")]
    public void RejectsDeviceExtendedUncAdsAndReservedDeviceForms(string destination)
    {
        Assert.Throws<SecurityException>(() =>
            ManagedWriteDestinationPolicy.CreateDevelopment().ApproveNewFile(destination));
    }

    [Fact]
    public void ExpandsExistingShortNameAliasesBeforeProtectedRootComparison()
    {
        var volume = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (string.IsNullOrWhiteSpace(volume)) return;
        var aliases = ProgramFilesShortNames
            .Select(alias => Path.Combine(volume, alias))
            .Where(Directory.Exists)
            .ToArray();
        if (aliases.Length == 0) return;
        var policy = ManagedWriteDestinationPolicy.CreateDevelopment();

        foreach (var alias in aliases)
        {
            var destination = Path.Combine(alias, $"not-created-{Guid.NewGuid():N}", "output");
            Assert.Throws<SecurityException>(() => policy.ApproveNewRoot(destination));
        }
    }

    [Fact]
    public void NewFileCapabilityRejectsNonexistentProtectedDescendant()
    {
        var testRoot = CreateTestRoot();
        var sourceRoot = Path.Combine(testRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            Assert.Throws<SecurityException>(() => ManagedWriteDestinationPolicy
                .CreateDevelopment([sourceRoot])
                .ApproveNewFile(Path.Combine(sourceRoot, "not-created", "report.json")));
            Assert.False(Directory.Exists(Path.Combine(sourceRoot, "not-created")));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(".novaemail-fixture-run.json")]
    [InlineData(".novaemail-source-manifest.json")]
    public async Task RejectsExistingProtectedMarkerParentWithoutChangingMarker(
        string markerName)
    {
        var testRoot = CreateTestRoot();
        var markerPath = Path.Combine(testRoot, markerName);
        await File.WriteAllBytesAsync(markerPath, "protected marker"u8.ToArray());
        var hashBefore = SHA256.HashData(await File.ReadAllBytesAsync(markerPath));
        var destination = Path.Combine(testRoot, $"not-created-{Guid.NewGuid():N}");
        try
        {
            Assert.Throws<SecurityException>(() =>
                ManagedWriteDestinationPolicy.CreateDevelopment().ApproveNewRoot(destination));
            Assert.False(Directory.Exists(destination));
            Assert.Equal(hashBefore, SHA256.HashData(await File.ReadAllBytesAsync(markerPath)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void CapabilitiesCannotBeConstructedThroughThePublicApi()
    {
        Assert.Empty(typeof(ManagedWriteDestination).GetConstructors());
        Assert.Empty(typeof(ManagedWriteFile).GetConstructors());
        Assert.Empty(typeof(ApprovedReadOnlyRoot).GetConstructors());
        Assert.Empty(typeof(ApprovedFixtureStoreRoot).GetConstructors());
        Assert.Empty(typeof(ManagedWriteDestinationPolicy).GetConstructors());
    }

    [Fact]
    public async Task FixtureStoreCapabilityDerivesOnlyTheFixedUncreatedRunChild()
    {
        var testRoot = CreateTestRoot();
        var marker = Path.Combine(testRoot, ".novaemail-fixture-run.json");
        await File.WriteAllTextAsync(marker, "{}", CancellationToken.None);
        try
        {
            var run = ApprovedReadOnlyRoot.AuthorizeExisting(testRoot);
            var approved = ApprovedFixtureStoreRoot.AuthorizeFixedChild(run);

            Assert.Equal(
                Path.Combine(Path.GetFullPath(testRoot), ".novaemail-test-store"),
                approved.ApprovedDataRoot);
            Assert.False(Directory.Exists(approved.ApprovedDataRoot));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void FixtureStoreCapabilityRejectsARunWithoutItsMarker()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var run = ApprovedReadOnlyRoot.AuthorizeExisting(testRoot);

            Assert.Throws<FileNotFoundException>(() =>
                ApprovedFixtureStoreRoot.AuthorizeFixedChild(run));
            Assert.False(Directory.Exists(Path.Combine(
                testRoot, ".novaemail-test-store")));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void ReadOnlyCapabilityCanonicalizesAnExistingLocalRoot()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var approved = ApprovedReadOnlyRoot.AuthorizeExisting(testRoot);

            Assert.Equal(Path.GetFullPath(testRoot), approved.ApprovedRoot);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"novaemail-managed-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
