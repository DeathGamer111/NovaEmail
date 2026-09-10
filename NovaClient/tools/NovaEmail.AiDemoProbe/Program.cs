using NovaEmail.Assistant;
using NovaEmail.Intelligence;
using NovaEmail.Safety;

return await RunAsync().ConfigureAwait(false);

static async Task<int> RunAsync()
{
    const string secretKey = "ai-api-key";
    const string identity = "synthetic-ai-demo-probe";
    var stage = "startup";
    try
    {
        var stored = new WindowsSecretVault().Read(secretKey) ??
            throw new InvalidOperationException(
                "No AI credential is enrolled.");
        var endpointText = Environment.GetEnvironmentVariable("NOVAEMAIL_AI_ENDPOINT")
            ?? "https://localhost:3000/";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("NOVAEMAIL_AI_ENDPOINT is not a valid absolute URI.");
        var model = Environment.GetEnvironmentVariable("NOVAEMAIL_AI_MODEL")
            ?? EmailIntelligenceProfile.DefaultModel;
        var profile = new EmailIntelligenceProfile(
            endpoint,
            model,
            Enabled: true);
        var source = new EmailIntelligenceSource(
            identity,
            "Synthetic local demo planning note",
            "sender@novaemail.test",
            new DateTimeOffset(2026, 8, 31, 14, 0, 0, TimeSpan.Zero),
            "This is fixed synthetic content for a controlled demo check. " +
            "Please confirm the planning call for Tuesday at 10:00 AM and bring the draft agenda.");
        using var transport = new OpenWebUiEmailIntelligenceTransport(
            new AssistantEndpointPolicy(), stored.Secret);
        var coordinator = new EmailIntelligenceCoordinator(transport);

        stage = "summary";
        var summaryNow = DateTimeOffset.UtcNow;
        var summary = await coordinator.SummarizeAsync(
            profile,
            source,
            new EmailIntelligenceConsent(
                identity,
                EmailIntelligenceOperation.Summarize,
                summaryNow,
                AllowMessageContentProcessing: true),
            summaryNow).ConfigureAwait(false);

        stage = "reply suggestions";
        var replyNow = DateTimeOffset.UtcNow;
        var replies = await coordinator.SuggestRepliesAsync(
            profile,
            source,
            new EmailIntelligenceConsent(
                identity,
                EmailIntelligenceOperation.SuggestReplies,
                replyNow,
                AllowMessageContentProcessing: true),
            replyNow).ConfigureAwait(false);

        stage = "draft revision";
        var revisionNow = DateTimeOffset.UtcNow;
        var revised = await coordinator.ReviseDraftAsync(
            profile,
            new EmailDraftRevisionSource(
                identity,
                "Synthetic local demo planning note",
                "I can attend. I will bring the draft agenda.",
                source),
            new EmailIntelligenceConsent(
                identity,
                EmailIntelligenceOperation.ReviseDraft,
                revisionNow,
                AllowMessageContentProcessing: true),
            revisionNow).ConfigureAwait(false);

        Console.WriteLine(
            "AI demo probe passed: synthetic-only=true; " +
            $"summaryCharacters={summary.Summary.Length}; " +
            $"actionItems={summary.ActionItems.Count}; " +
            $"replySuggestions={replies.Count}; " +
            $"revisedDraftCharacters={revised.Length}.");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"AI demo probe failed safely during {stage} ({exception.GetType().Name}); " +
            "no response content was emitted.");
        return 1;
    }
}
