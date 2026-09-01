using System.Reflection;
using NovaEmail.Safety;
using NovaEmail.Storage;

namespace NovaEmail.Storage.Tests;

public sealed class ManagedStorageBoundaryTests
{
    [Fact]
    public void ModernStoreExposesOnlyTheFixedDevelopmentFactory()
    {
        Assert.Empty(typeof(ModernMailStore).GetConstructors());
        var factory = typeof(ModernMailStore).GetMethod(
            nameof(ModernMailStore.CreateDevelopmentStore),
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(factory);
        Assert.Equal(typeof(ModernMailStore), factory.ReturnType);
        Assert.Empty(factory.GetParameters());
    }

    [Fact]
    public void ProtectedBackupAndRestoreRequireOpaqueWriteCapabilities()
    {
        var backup = typeof(ModernMailBackupService).GetMethod(
            nameof(ModernMailBackupService.CreateProtectedAsync),
            BindingFlags.Public | BindingFlags.Static);
        var restore = typeof(ModernMailBackupService).GetMethod(
            nameof(ModernMailBackupService.RestoreProtectedAsync),
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(backup);
        Assert.NotNull(restore);
        Assert.Equal(typeof(ManagedWriteDestination), backup.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(ManagedWriteDestination), restore.GetParameters()[1].ParameterType);
    }
}
