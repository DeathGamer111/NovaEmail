using System.Text.Json;

namespace NovaEmail.Safety.Tests;

public sealed class DevelopmentAuditLogTests
{
    [Fact]
    public void WritesOnlyBoundedFixedEventDataBeneathExplicitRoot()
    {
        var root = CreateTestRoot();
        try
        {
            var timestamp = new DateTimeOffset(2026, 8, 13, 12, 34, 56, TimeSpan.Zero);
            Assert.True(DevelopmentAuditLog.WriteEvent(root, "CLIENT_STARTED", timestamp));
            var path = Assert.Single(Directory.GetFiles(root, "*.jsonl"));
            var line = Assert.Single(File.ReadAllLines(path));
            using var document = JsonDocument.Parse(line);
            Assert.Equal("CLIENT_STARTED", document.RootElement.GetProperty("event").GetString());
            Assert.Equal(ApplicationIdentity.PackageIdentityName,
                document.RootElement.GetProperty("identity").GetString());
            Assert.Equal(timestamp, document.RootElement.GetProperty("utc").GetDateTimeOffset());
            Assert.Equal(3, document.RootElement.EnumerateObject().Count());
            Assert.Throws<ArgumentException>(() =>
                DevelopmentAuditLog.WriteEvent(root, "PASSWORD=not-allowed", timestamp));
            Assert.Single(File.ReadAllLines(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Modern")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException();
        var root = Path.Combine(
            directory.FullName, ".artifacts", "audit-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
