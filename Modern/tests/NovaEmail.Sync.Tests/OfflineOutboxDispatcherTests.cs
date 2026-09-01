using MimeKit;
using NovaEmail.Mail;
using NovaEmail.Safety;
using NovaEmail.Storage;
using NovaEmail.Testing;

namespace NovaEmail.Sync.Tests;

public sealed class OfflineOutboxDispatcherTests
{
    [Fact]
    public async Task QueuedAccountCannotBeDispatchedThroughAnotherAccountEndpoint()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueAsync(store, "account-bound");
            var transport = new FakeTransport();
            var dispatcher = new OfflineOutboxDispatcher(store, transport);

            Assert.Null(await dispatcher.DispatchNextAsync("account-b", Endpoint, Credentials));
            Assert.Equal(0, transport.SendCount);
            Assert.Equal(OperationEventState.Queued,
                await store.GetLatestOperationStateAsync(queued.OperationId));

            var result = await dispatcher.DispatchNextAsync(AccountId, Endpoint, Credentials);
            Assert.Equal(OperationEventState.Acknowledged, result!.State);
            Assert.Equal(1, transport.SendCount);
            var sent = Assert.Single(await store.ReadStoredFolderSummariesAsync());
            Assert.True(sent.IsSentItems);
            var retained = Assert.Single(
                (await store.ListStoredFolderMessagesPageAsync(sent.FolderId, 10)).Messages);
            Assert.Equal(
                await store.ReadOutboundMimeAsync(queued.OperationId),
                await store.ReadStoredMimeAsync(retained.MessageId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AcknowledgedMessageIsNeverDispatchedTwice()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueAsync(store, "acknowledged");
            var transport = new FakeTransport();
            var dispatcher = new OfflineOutboxDispatcher(store, transport);
            var result = await dispatcher.DispatchNextAsync(AccountId, Endpoint, Credentials);

            Assert.Equal(OperationEventState.Acknowledged, result!.State);
            Assert.Equal(1, transport.SendCount);
            Assert.Null(await dispatcher.DispatchNextAsync(AccountId, Endpoint, Credentials));
            Assert.Equal(OperationEventState.Acknowledged, await store.GetLatestOperationStateAsync(queued.OperationId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownSubmissionBecomesConflictAndIsNotRetried()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueAsync(store, "unknown");
            var transport = new FakeTransport(new MailDeliveryException(
                MailSubmissionCertainty.Unknown, "unknown", new IOException("disconnect")));
            var dispatcher = new OfflineOutboxDispatcher(store, transport);
            var result = await dispatcher.DispatchNextAsync(AccountId, Endpoint, Credentials);

            Assert.Equal(OperationEventState.Conflict, result!.State);
            Assert.Null(await dispatcher.DispatchNextAsync(AccountId, Endpoint, Credentials));
            Assert.Equal(OperationEventState.Conflict, await store.GetLatestOperationStateAsync(queued.OperationId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CanceledTransportOutcomeBecomesConflictAndIsNeverRetried()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueAsync(store, "canceled-outcome");
            var transport = new FakeTransport(new OperationCanceledException("transport canceled"));
            var dispatcher = new OfflineOutboxDispatcher(store, transport);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                dispatcher.DispatchNextAsync(AccountId, Endpoint, Credentials));

            Assert.Equal(1, transport.SendCount);
            Assert.Equal(OperationEventState.Conflict,
                await store.GetLatestOperationStateAsync(queued.OperationId));
            Assert.Null(await dispatcher.DispatchNextAsync(AccountId, Endpoint, Credentials));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialsAndServerControlledTextNeverEnterDispatchResultsOrJournal()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            await QueueAsync(store, "redacted-acknowledgement");
            var reflectedSecret = "reflected-secret-" + Guid.NewGuid().ToString("N");
            var dispatcher = new OfflineOutboxDispatcher(
                store, new FakeTransport(response: reflectedSecret));

            var result = await dispatcher.DispatchNextAsync(AccountId, Endpoint, Credentials);
            var operation = Assert.Single(await store.ReadOutboundOperationSummariesAsync());

            Assert.Equal(OperationEventState.Acknowledged, result!.State);
            Assert.DoesNotContain(reflectedSecret, result.Detail!, StringComparison.Ordinal);
            Assert.DoesNotContain(Credentials.UserName, result.Detail!, StringComparison.Ordinal);
            Assert.DoesNotContain(Credentials.Password, result.Detail!, StringComparison.Ordinal);
            Assert.NotNull(operation.ServerAcknowledgement);
            Assert.DoesNotContain(
                reflectedSecret, operation.ServerAcknowledgement!, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Credentials.Password, operation.ServerAcknowledgement!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TransportExceptionTextNeverEntersDispatchResult()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            await QueueAsync(store, "redacted-transport-error");
            var reflectedSecret = "reflected-error-" + Guid.NewGuid().ToString("N");
            var transport = new FakeTransport(new MailDeliveryException(
                MailSubmissionCertainty.NotSubmitted,
                reflectedSecret,
                new IOException(reflectedSecret)));
            var dispatcher = new OfflineOutboxDispatcher(store, transport);

            var result = await dispatcher.DispatchNextAsync(AccountId, Endpoint, Credentials);

            Assert.Equal(OperationEventState.RetryScheduled, result!.State);
            Assert.DoesNotContain(reflectedSecret, result.Detail!, StringComparison.Ordinal);
            Assert.DoesNotContain(Credentials.Password, result.Detail!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptedAttemptIsQuarantinedInsteadOfAutomaticallyResubmitted()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueAsync(store, "interrupted");
            await store.AppendOperationEventAsync(queued.OperationId, OperationEventState.Attempting);
            var transport = new FakeTransport();
            var dispatcher = new OfflineOutboxDispatcher(store, transport);

            Assert.Null(await dispatcher.DispatchNextAsync(AccountId, Endpoint, Credentials));
            Assert.Equal(0, transport.SendCount);
            Assert.Equal(OperationEventState.Conflict, await store.GetLatestOperationStateAsync(queued.OperationId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AcknowledgedMessageUsesOneRealSmtpSessionAndIsNeverResubmitted()
    {
        var root = CreateTestRoot();
        await using var server = ScriptedSmtpsServer.Start(ScriptedSmtpScenario.Accept);
        try
        {
            var store = await CreateStoreAsync(root);
            var queued = await QueueAsync(store, "scripted-acknowledged");
            var transport = new SecureMailTransport(
                new EndpointPolicy(), new CertificateTrustPolicy([server.Certificate]));
            var dispatcher = new OfflineOutboxDispatcher(store, transport);

            var result = await dispatcher.DispatchNextAsync(
                AccountId,
                new SecureMailEndpoint("localhost", server.Port, TlsConnectionMode.ImplicitTls),
                Credentials);
            Assert.Null(await dispatcher.DispatchNextAsync(
                AccountId,
                new SecureMailEndpoint("localhost", server.Port, TlsConnectionMode.ImplicitTls),
                Credentials));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.WaitForCompletionAsync(timeout.Token);

            Assert.Equal(OperationEventState.Acknowledged, result!.State);
            Assert.Equal(OperationEventState.Acknowledged,
                await store.GetLatestOperationStateAsync(queued.OperationId));
            Assert.Equal(1, server.ConnectionCount);
            Assert.Equal(1, server.MessageCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitEnvelopeRoutesHeaderlessMessageExactlyOnce()
    {
        var root = CreateTestRoot();
        try
        {
            var store = await CreateStoreAsync(root);
            var message = new MimeMessage
            {
                MessageId = "headerless-route@example.test",
                Subject = "Opaque message payload",
                Body = new TextPart("plain") { Text = "Explicit envelope routing test." },
            };
            message.From.Add(MailboxAddress.Parse("sender@example.test"));
            await using var stream = new MemoryStream();
            await message.WriteToAsync(stream);
            var queued = await store.QueueOutboundMessageAsync(
                AccountId,
                "headerless-route",
                message.MessageId,
                stream.ToArray(),
                "sender@example.test",
                ["receiver@example.test"]);
            var transport = new FakeTransport();
            var dispatcher = new OfflineOutboxDispatcher(store, transport);

            var result = await dispatcher.DispatchNextAsync(AccountId, Endpoint, Credentials);

            Assert.Equal(OperationEventState.Acknowledged, result!.State);
            Assert.Equal(1, transport.ExplicitEnvelopeSendCount);
            Assert.Equal("sender@example.test", transport.EnvelopeSender?.Address);
            Assert.Equal("receiver@example.test", Assert.Single(transport.EnvelopeRecipients).Address);
            Assert.Null(await dispatcher.DispatchNextAsync(AccountId, Endpoint, Credentials));
            Assert.Equal(OperationEventState.Acknowledged,
                await store.GetLatestOperationStateAsync(queued.OperationId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitEnvelopeRoutesHeaderlessMessageOverRealTlsSmtp()
    {
        var root = CreateTestRoot();
        await using var server = ScriptedSmtpsServer.Start(ScriptedSmtpScenario.Accept);
        try
        {
            var store = await CreateStoreAsync(root);
            var message = new MimeMessage
            {
                MessageId = "headerless-real-smtp@example.test",
                Subject = "Opaque message payload",
                Body = new TextPart("plain") { Text = "Explicit SMTP envelope test." },
            };
            message.From.Add(MailboxAddress.Parse("sender@example.test"));
            await using var stream = new MemoryStream();
            await message.WriteToAsync(stream);
            var queued = await store.QueueOutboundMessageAsync(
                AccountId,
                "headerless-real-smtp",
                message.MessageId,
                stream.ToArray(),
                "sender@example.test",
                ["to@example.test", "hidden@example.test"]);
            var transport = new SecureMailTransport(
                new EndpointPolicy(), new CertificateTrustPolicy([server.Certificate]));
            var dispatcher = new OfflineOutboxDispatcher(store, transport);

            var result = await dispatcher.DispatchNextAsync(
                AccountId,
                new SecureMailEndpoint("localhost", server.Port, TlsConnectionMode.ImplicitTls),
                Credentials);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.WaitForCompletionAsync(timeout.Token);

            Assert.Equal(OperationEventState.Acknowledged, result!.State);
            Assert.Equal(OperationEventState.Acknowledged,
                await store.GetLatestOperationStateAsync(queued.OperationId));
            Assert.Single(server.Commands, command =>
                command.StartsWith("MAIL FROM:<sender@example.test>", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, server.Commands.Count(command =>
                command.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(server.Commands, command =>
                command.StartsWith("RCPT TO:<to@example.test>", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(server.Commands, command =>
                command.StartsWith("RCPT TO:<hidden@example.test>", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("hidden@example.test", server.LastMessage,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, server.MessageCount);
            Assert.Null(await dispatcher.DispatchNextAsync(
                AccountId,
                new SecureMailEndpoint("localhost", server.Port, TlsConnectionMode.ImplicitTls),
                Credentials));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly SecureMailEndpoint Endpoint =
        new("localhost", 3465, TlsConnectionMode.ImplicitTls);
    private static readonly MailCredentials Credentials = new("local", "local");
    private const string AccountId = "account-a";

    private static async Task<QueuedOutboundMessage> QueueAsync(ModernMailStore store, string key)
    {
        var message = OutboundMessageComposer.Compose(
            new OutboundDraft(
                key, MailboxAddress.Parse("sender@example.test"),
                [MailboxAddress.Parse("receiver@example.test")], [], [], "subject", "body", null, []),
            DateTimeOffset.UtcNow);
        await using var stream = new MemoryStream();
        await message.WriteToAsync(stream);
        return await store.QueueOutboundMessageAsync(
            AccountId, key, message.MessageId!, stream.ToArray());
    }

    private static async Task<ModernMailStore> CreateStoreAsync(string root)
    {
        var store = new ModernMailStore(Path.Combine(root, "mail.db"), root);
        await store.InitializeAsync();
        return store;
    }

    private static string CreateTestRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Modern")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException();
        var root = Path.Combine(directory.FullName, ".artifacts", "outbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FakeTransport(Exception? exception = null, string response = "accepted")
        : ISecureMailTransport
    {
        public int SendCount { get; private set; }
        public int ExplicitEnvelopeSendCount { get; private set; }
        public MailboxAddress? EnvelopeSender { get; private set; }
        public IReadOnlyCollection<MailboxAddress> EnvelopeRecipients { get; private set; } = [];

        public Task<string> SendAsync(
            SecureMailEndpoint endpoint,
            MailCredentials credentials,
            MimeMessage message,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            return exception is null ? Task.FromResult(response) : Task.FromException<string>(exception);
        }

        public Task<string> SendAsync(
            SecureMailEndpoint endpoint,
            MailCredentials credentials,
            MimeMessage message,
            MailboxAddress envelopeSender,
            IReadOnlyCollection<MailboxAddress> envelopeRecipients,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            ExplicitEnvelopeSendCount++;
            EnvelopeSender = envelopeSender;
            EnvelopeRecipients = envelopeRecipients;
            return exception is null ? Task.FromResult(response) : Task.FromException<string>(exception);
        }

        public Task<IReadOnlyList<RemoteMessageDescriptor>> InspectImapInboxAsync(
            SecureMailEndpoint endpoint, MailCredentials credentials, int maximumCount,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteMessageDescriptor>> InspectPopInboxAsync(
            SecureMailEndpoint endpoint, MailCredentials credentials, int maximumCount,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
