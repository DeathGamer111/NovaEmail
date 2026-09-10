using NovaEmail.Storage;

namespace NovaEmail.Storage.Tests;

public sealed class MailboxPresentationStateStoreTests
{
    [Fact]
    public void StateRoundTripsThroughBoundedAtomicLocalFile()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "settings", "mailbox-state.json");
        var store = new MailboxPresentationStateStore(path);

        var absent = store.Load();
        Assert.Empty(absent.State.Messages);
        Assert.Empty(absent.State.Labels);
        Assert.False(absent.UsedFallback);

        store.Save(new MailboxPresentationState(
            new Dictionary<string, MailboxMessagePresentationState>
            {
                ["message-1"] = new("Archive", IsRead: true, IsFollowUp: false),
                ["message-2"] = new(string.Empty, IsRead: false, IsFollowUp: true),
            },
            [new MailboxLabelDefinition("label-1", "Project", "launch plan")]));

        var loaded = store.Load();

        Assert.False(loaded.UsedFallback);
        Assert.Equal("Archive", loaded.State.Messages["message-1"].Folder);
        Assert.True(loaded.State.Messages["message-1"].IsRead);
        Assert.True(loaded.State.Messages["message-2"].IsFollowUp);
        Assert.Equal("Project", Assert.Single(loaded.State.Labels).DisplayName);
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(path)!), file =>
            file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"schemaVersion\":999,\"messages\":{},\"labels\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"messages\":{},\"labels\":[{\"id\":\"same\",\"displayName\":\"One\",\"filterText\":\"a\"},{\"id\":\"same\",\"displayName\":\"Two\",\"filterText\":\"b\"}]}")]
    public void InvalidStateFailsClosedWithoutOverwritingEvidence(string content)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "mailbox-state.json");
        File.WriteAllText(path, content);

        var loaded = new MailboxPresentationStateStore(path).Load();

        Assert.True(loaded.UsedFallback);
        Assert.Empty(loaded.State.Messages);
        Assert.Empty(loaded.State.Labels);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void OversizedStateFailsClosedWithoutParsingOrMutation()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "mailbox-state.json");
        var content = new string('x', MailboxPresentationStateStore.MaximumSettingsBytes + 1);
        File.WriteAllText(path, content);

        var loaded = new MailboxPresentationStateStore(path).Load();

        Assert.True(loaded.UsedFallback);
        Assert.Equal(content.Length, new FileInfo(path).Length);
    }

    [Fact]
    public void SaveRejectsControlCharactersBeforeCreatingAFile()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "mailbox-state.json");
        var state = new MailboxPresentationState(
            new Dictionary<string, MailboxMessagePresentationState>(),
            [new MailboxLabelDefinition("label-1", "Bad\nLabel", "filter")]);

        Assert.Throws<InvalidDataException>(() => new MailboxPresentationStateStore(path).Save(state));
        Assert.False(File.Exists(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"novaemail-mailbox-state-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
