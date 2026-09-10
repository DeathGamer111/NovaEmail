using NovaEmail.Mail;
using NovaEmail.Safety;
using NovaEmail.Testing;

namespace NovaEmail.Sync.Tests;

public sealed class MailKitImapFolderCatalogTests
{
    [Fact]
    public async Task RealMailKitCatalogPreservesHierarchySelectabilityAndSpecialUseReadOnly()
    {
        await using var server = ScriptedImapsServer.Start(ScriptedImapScenario.FolderCatalog);
        var catalog = CreateCatalog(server);

        var entries = await catalog.ReadAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.WaitForCompletionAsync(timeout.Token);

        Assert.Equal(4, entries.Count);
        var inbox = entries.Single(entry => entry.FullName == "INBOX");
        Assert.True(inbox.IsInbox);
        Assert.True(inbox.IsSelectable);
        Assert.True(inbox.IsSubscribed);
        var archive = entries.Single(entry => entry.FullName == "Archive");
        Assert.Contains("Archive", archive.SpecialUses);
        var parent = entries.Single(entry => entry.FullName == "Projects");
        Assert.False(parent.IsSelectable);
        Assert.True(parent.HasChildren);
        Assert.True(entries.Single(entry => entry.FullName == "Projects/2026").IsSelectable);
        Assert.Contains(server.Commands, command =>
            command.Contains(" LIST ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(server.Commands, IsMutationCommand);
        Assert.DoesNotContain(server.Commands, command =>
            command.Contains("local-password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OversizedCatalogEntryIsRejectedWithoutAnyServerMutation()
    {
        await using var server = ScriptedImapsServer.Start(ScriptedImapScenario.OversizedFolderCatalogEntry);
        var catalog = CreateCatalog(server);

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ReadAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.WaitForCompletionAsync(timeout.Token);

        Assert.DoesNotContain(server.Commands, IsMutationCommand);
    }

    [Fact]
    public async Task UnresolvableHostFailsBeforeFolderDiscoveryAuthenticates()
    {
        var catalog = new MailKitImapFolderCatalog(
            new SecureMailEndpoint("mail.company.example", 993, TlsConnectionMode.ImplicitTls),
            new MailCredentials("unused", "unused"),
            new EndpointPolicy(),
            new CertificateTrustPolicy());

        await Assert.ThrowsAsync<IOException>(() => catalog.ReadAsync());
    }

    private static MailKitImapFolderCatalog CreateCatalog(ScriptedImapsServer server) =>
        new(
            new SecureMailEndpoint("localhost", server.Port, TlsConnectionMode.ImplicitTls),
            new MailCredentials("local-user", "local-password"),
            new EndpointPolicy(),
            new CertificateTrustPolicy([server.Certificate]));

    private static bool IsMutationCommand(string command) =>
        command.Contains(" CREATE ", StringComparison.OrdinalIgnoreCase) ||
        command.Contains(" DELETE ", StringComparison.OrdinalIgnoreCase) ||
        command.Contains(" RENAME ", StringComparison.OrdinalIgnoreCase) ||
        command.Contains(" SUBSCRIBE ", StringComparison.OrdinalIgnoreCase) ||
        command.Contains(" UNSUBSCRIBE ", StringComparison.OrdinalIgnoreCase) ||
        command.Contains(" STORE ", StringComparison.OrdinalIgnoreCase) ||
        command.Contains(" COPY ", StringComparison.OrdinalIgnoreCase) ||
        command.Contains(" MOVE ", StringComparison.OrdinalIgnoreCase) ||
        command.Contains(" EXPUNGE", StringComparison.OrdinalIgnoreCase) ||
        command.Contains(" CLOSE", StringComparison.OrdinalIgnoreCase);
}
