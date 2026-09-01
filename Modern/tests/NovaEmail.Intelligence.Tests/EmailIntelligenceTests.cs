using System.Security;
using System.Text.Json;
using NovaEmail.Intelligence;

namespace NovaEmail.Intelligence.Tests;

public sealed class EmailIntelligenceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 22, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultProfilePinsQwenAndRemainsDisabled()
    {
        var profile = EmailIntelligenceProfile.DisabledLocalDefault();

        Assert.Equal("qwen2.5:7b", profile.Model);
        Assert.Equal("https://localhost:3000/", profile.Endpoint.AbsoluteUri);
        Assert.False(profile.Enabled);
        profile.Validate();
    }

    [Fact]
    public void EnabledProfileAllowsLoopbackOrPublicDefaultHttpsPort()
    {
        var profile = new EmailIntelligenceProfile(
            new Uri("https://ai.example.test/"), EmailIntelligenceProfile.DefaultModel, Enabled: true);

        profile.Validate();
        new EmailIntelligenceProfile(
            new Uri("https://localhost:9443/"), "custom-model", Enabled: true).Validate();
        Assert.Throws<SecurityException>(() => new EmailIntelligenceProfile(
            new Uri("https://ai.example.test:444/"), EmailIntelligenceProfile.DefaultModel, Enabled: true)
            .Validate());
    }

    [Fact]
    public void EnabledAlternateModelIsAllowed()
    {
        var profile = new EmailIntelligenceProfile(
            new Uri("https://localhost:9443/"), "function-model", Enabled: true);

        profile.Validate();
    }

    [Fact]
    public void AiProfileRejectsPathsCredentialsAndMissingValues()
    {
        Assert.Throws<SecurityException>(() => new EmailIntelligenceProfile(
            new Uri("https://localhost:9443/api/chat"), "qwen2.5:7b", Enabled: false)
            .Validate());
        Assert.Throws<SecurityException>(() => new EmailIntelligenceProfile(
            new Uri("https://user:secret@localhost:9443/"), "qwen2.5:7b", Enabled: false)
            .Validate());
        Assert.Throws<SecurityException>(() => new EmailIntelligenceProfile(
            null!, "qwen2.5:7b", Enabled: false).Validate());
        Assert.Throws<InvalidDataException>(() => new EmailIntelligenceProfile(
            new Uri("https://localhost:9443/"), " ", Enabled: false)
            .Validate());
    }

    [Fact]
    public void LocalScannerFindsIsoNamedAndUsDatesWithoutAi()
    {
        var result = CalendarEvidenceScanner.Scan(new CalendarScanInput(
            "message-1",
            "Quarterly planning",
            "Kickoff is 2026-09-15 14:30. Review on October 2, 2026 at 9:15 AM. " +
            "Backup date 10/05/26 16:45.",
            Now));

        Assert.False(result.InputTruncated);
        Assert.Collection(result.Suggestions,
            item => Assert.Equal(new DateTime(2026, 9, 15, 14, 30, 0), item.StartLocal),
            item => Assert.Equal(new DateTime(2026, 10, 2, 9, 15, 0), item.StartLocal),
            item => Assert.Equal(new DateTime(2026, 10, 5, 16, 45, 0), item.StartLocal));
        Assert.All(result.Suggestions, item =>
        {
            Assert.True(item.RequiresUserConfirmation);
            Assert.False(item.AiEnriched);
            Assert.Equal("Quarterly planning", item.SuggestedTitle);
            Assert.Matches("^[0-9a-f]{64}$", item.EvidenceId);
        });
    }

    [Fact]
    public void MissingYearRollsForwardOnlyWhenDateIsClearlyPast()
    {
        var result = CalendarEvidenceScanner.Scan(new CalendarScanInput(
            "message-2", "Planning", "Meet January 5 at 2 PM.",
            new DateTimeOffset(2026, 12, 20, 8, 0, 0, TimeSpan.Zero)));

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(new DateTime(2027, 1, 5, 14, 0, 0), suggestion.StartLocal);
    }

    [Fact]
    public void InvalidDatesAreIgnoredAndDateOnlyRemainsAllDay()
    {
        var result = CalendarEvidenceScanner.Scan(new CalendarScanInput(
            "message-3", "Dates", "Invalid 2026-02-31; valid 2026-03-31.", Now));

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(new DateTime(2026, 3, 31), suggestion.StartLocal);
        Assert.True(suggestion.AllDay);
    }

    [Fact]
    public void ScannerBoundsInputAndSuggestionCount()
    {
        var body = string.Join(' ', Enumerable.Range(1, 40)
            .Select(index => $"2026-09-{(index % 28) + 1:D2}")) +
            new string('x', CalendarEvidenceScanner.MaximumInputCharacters);

        var result = CalendarEvidenceScanner.Scan(new CalendarScanInput(
            "message-4", "Many dates", body, Now));

        Assert.True(result.InputTruncated);
        Assert.Equal(CalendarEvidenceScanner.MaximumSuggestions, result.Suggestions.Count);
        Assert.InRange(result.ScannedCharacters, 1, CalendarEvidenceScanner.MaximumInputCharacters);
    }

    [Fact]
    public async Task SummaryRequiresEnabledProfileAndFreshExactConsent()
    {
        var transport = new RecordingTransport("""
            {"summary":"Summary","actionItems":[],"uncertainty":null}
            """);
        var coordinator = new EmailIntelligenceCoordinator(transport);
        var source = Source();
        var consent = Consent(EmailIntelligenceOperation.Summarize);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.SummarizeAsync(
                EmailIntelligenceProfile.DisabledLocalDefault(), source, consent, Now));
        await Assert.ThrowsAsync<SecurityException>(async () =>
            await coordinator.SummarizeAsync(
                EnabledProfile(), source, consent with { GrantedUtc = Now.AddMinutes(-6) }, Now));
        await Assert.ThrowsAsync<SecurityException>(async () =>
            await coordinator.SummarizeAsync(
                EnabledProfile(), source,
                consent with { MessageIdentity = "different-message" }, Now));
        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task SummaryRequestIsBoundedHashedAndTreatsMailAsUntrusted()
    {
        var transport = new RecordingTransport("""
            {"summary":"A concise summary.","actionItems":["Reply by Friday"],"uncertainty":"Time zone unspecified"}
            """);
        var coordinator = new EmailIntelligenceCoordinator(transport);

        var result = await coordinator.SummarizeAsync(
            EnabledProfile(), Source(), Consent(EmailIntelligenceOperation.Summarize), Now);

        Assert.Equal("A concise summary.", result.Summary);
        Assert.Equal(["Reply by Friday"], result.ActionItems);
        var request = Assert.IsType<EmailIntelligenceRequest>(transport.LastRequest);
        Assert.Equal("qwen2.5:7b", request.Profile.Model);
        Assert.Contains("untrusted data", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", request.InputSha256);
        using var input = JsonDocument.Parse(request.InputJson);
        Assert.Equal("message-1", input.RootElement.GetProperty("MessageIdentity").GetString());
        Assert.False(input.RootElement.TryGetProperty("Attachments", out _));
        Assert.False(input.RootElement.TryGetProperty("Bcc", out _));
    }

    [Fact]
    public async Task BoundedInputNeverSplitsUnicodeSurrogatePair()
    {
        var transport = new RecordingTransport("""
            {"summary":"Summary","actionItems":[],"uncertainty":null}
            """);
        var coordinator = new EmailIntelligenceCoordinator(transport);
        var body = new string('x', EmailIntelligenceCoordinator.MaximumInputCharacters - 1) +
            "📅" + new string('y', 20);

        await coordinator.SummarizeAsync(
            EnabledProfile(), Source() with { PlainText = body },
            Consent(EmailIntelligenceOperation.Summarize), Now);

        var request = Assert.IsType<EmailIntelligenceRequest>(transport.LastRequest);
        using var input = JsonDocument.Parse(request.InputJson);
        var retained = input.RootElement.GetProperty("PlainText").GetString();
        Assert.NotNull(retained);
        Assert.Equal(EmailIntelligenceCoordinator.MaximumInputCharacters - 1, retained.Length);
        Assert.DoesNotContain('\ud83d', retained);
        Assert.DoesNotContain('\udcc5', retained);
    }

    [Fact]
    public async Task UnknownSummaryFieldsFailClosed()
    {
        var coordinator = new EmailIntelligenceCoordinator(new RecordingTransport("""
            {"summary":"Summary","actionItems":[],"uncertainty":null,"tool":"send-mail"}
            """));

        await Assert.ThrowsAsync<JsonException>(async () =>
            await coordinator.SummarizeAsync(
                EnabledProfile(), Source(), Consent(EmailIntelligenceOperation.Summarize), Now));
    }

    [Fact]
    public async Task ReplySuggestionsAreLimitedAndCannotSend()
    {
        var transport = new RecordingTransport("""
            {"replies":[{"label":"Brief","body":"Thanks, I can attend."},{"label":"Decline","body":"I cannot attend."}]}
            """);
        var coordinator = new EmailIntelligenceCoordinator(transport);

        var replies = await coordinator.SuggestRepliesAsync(
            EnabledProfile(), Source(), Consent(EmailIntelligenceOperation.SuggestReplies), Now);

        Assert.Equal(2, replies.Count);
        Assert.Equal("Brief", replies[0].Label);
        Assert.Contains("exactly this schema", transport.LastRequest!.SystemInstruction);
        Assert.Contains("Do not put label or body at the root", transport.LastRequest.SystemInstruction);
    }

    [Fact]
    public async Task MoreThanThreeReplySuggestionsFailClosed()
    {
        var coordinator = new EmailIntelligenceCoordinator(new RecordingTransport("""
            {"replies":[
              {"label":"1","body":"One"},{"label":"2","body":"Two"},
              {"label":"3","body":"Three"},{"label":"4","body":"Four"}]}
            """));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.SuggestRepliesAsync(
                EnabledProfile(), Source(), Consent(EmailIntelligenceOperation.SuggestReplies), Now));
    }

    [Fact]
    public async Task DraftRevisionIncludesUserTextAndOptionalReplyContext()
    {
        var transport = new RecordingTransport("""
            {"revisedDraft":"Thanks, I can attend Thursday."}
            """);
        var coordinator = new EmailIntelligenceCoordinator(transport);
        var source = new EmailDraftRevisionSource(
            "message-1",
            "Re: Planning",
            "thanks i can make it",
            Source());

        var revised = await coordinator.ReviseDraftAsync(
            EnabledProfile(),
            source,
            Consent(EmailIntelligenceOperation.ReviseDraft),
            Now);

        Assert.Equal("Thanks, I can attend Thursday.", revised);
        var request = Assert.IsType<EmailIntelligenceRequest>(transport.LastRequest);
        Assert.Equal(EmailIntelligenceOperation.ReviseDraft, request.Operation);
        Assert.Contains("Rewrite only the user's draft", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("use no tools", request.SystemInstruction, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(request.InputJson))),
            request.InputSha256);
        using var input = JsonDocument.Parse(request.InputJson);
        Assert.Equal(
            "thanks i can make it",
            input.RootElement.GetProperty("DraftPlainText").GetString());
        Assert.Equal(
            Source().PlainText,
            input.RootElement.GetProperty("ContextMessage").GetProperty("PlainText").GetString());
        Assert.False(input.RootElement.TryGetProperty("Attachments", out _));
        Assert.False(input.RootElement.TryGetProperty("Recipients", out _));
    }

    [Fact]
    public async Task DraftRevisionSupportsNewComposeWithoutReplyContext()
    {
        var transport = new RecordingTransport("""
            {"revisedDraft":"Please review the attached proposal."}
            """);
        var coordinator = new EmailIntelligenceCoordinator(transport);
        var source = new EmailDraftRevisionSource(
            "draft:abc123",
            "Proposal",
            "can you review this",
            ContextMessage: null);
        var consent = new EmailIntelligenceConsent(
            source.MessageIdentity,
            EmailIntelligenceOperation.ReviseDraft,
            Now,
            AllowMessageContentProcessing: true);

        var revised = await coordinator.ReviseDraftAsync(
            EnabledProfile(), source, consent, Now);

        Assert.Equal("Please review the attached proposal.", revised);
        using var input = JsonDocument.Parse(transport.LastRequest!.InputJson);
        Assert.Equal(JsonValueKind.Null, input.RootElement.GetProperty("ContextMessage").ValueKind);
    }

    [Fact]
    public async Task DraftRevisionRequiresExactConsentContextBindingAndBounds()
    {
        var transport = new RecordingTransport("""
            {"revisedDraft":"Revised"}
            """);
        var coordinator = new EmailIntelligenceCoordinator(transport);
        var source = new EmailDraftRevisionSource(
            "message-1",
            "Re: Planning",
            "My draft",
            Source());

        await Assert.ThrowsAsync<SecurityException>(async () =>
            await coordinator.ReviseDraftAsync(
                EnabledProfile(),
                source,
                Consent(EmailIntelligenceOperation.SuggestReplies),
                Now));
        await Assert.ThrowsAsync<SecurityException>(async () =>
            await coordinator.ReviseDraftAsync(
                EnabledProfile(),
                source with { ContextMessage = Source() with { MessageIdentity = "other-message" } },
                Consent(EmailIntelligenceOperation.ReviseDraft),
                Now));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.ReviseDraftAsync(
                EnabledProfile(),
                source with
                {
                    DraftPlainText = new string(
                        'x', EmailIntelligenceCoordinator.MaximumDraftCharacters + 1),
                },
                Consent(EmailIntelligenceOperation.ReviseDraft),
                Now));
        Assert.Null(transport.LastRequest);
    }

    [Fact]
    public async Task DraftRevisionUnknownOutputFailsClosed()
    {
        var coordinator = new EmailIntelligenceCoordinator(new RecordingTransport("""
            {"revisedDraft":"Revised","send":true}
            """));
        var source = new EmailDraftRevisionSource(
            "message-1", "Re: Planning", "My draft", Source());

        await Assert.ThrowsAsync<JsonException>(async () =>
            await coordinator.ReviseDraftAsync(
                EnabledProfile(),
                source,
                Consent(EmailIntelligenceOperation.ReviseDraft),
                Now));
    }

    [Fact]
    public async Task CalendarEnrichmentMustReferenceLocalEvidence()
    {
        var evidence = CalendarEvidenceScanner.Scan(new CalendarScanInput(
            "message-1", "Meeting", "Meet 2026-09-15 14:30.", Now)).Suggestions;
        var coordinator = new EmailIntelligenceCoordinator(new RecordingTransport($$"""
            {"suggestions":[{"evidenceId":"{{evidence[0].EvidenceId}}","title":"Meeting","start":"2026-09-15T14:30:00-05:00","end":"2026-09-15T15:00:00-05:00","location":null,"confidence":0.92}]}
            """));

        var suggestions = await coordinator.EnrichCalendarAsync(
            EnabledProfile(), Source(), Consent(EmailIntelligenceOperation.EnrichCalendarCandidates),
            evidence, Now);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(evidence[0].EvidenceId, suggestion.EvidenceId);
        Assert.True(suggestion.RequiresUserConfirmation);
    }

    [Fact]
    public async Task CalendarEnrichmentRejectsInventedEvidence()
    {
        var evidence = CalendarEvidenceScanner.Scan(new CalendarScanInput(
            "message-1", "Meeting", "Meet 2026-09-15 14:30.", Now)).Suggestions;
        var coordinator = new EmailIntelligenceCoordinator(new RecordingTransport("""
            {"suggestions":[{"evidenceId":"invented","title":"Meeting","start":"2026-09-15T14:30:00-05:00","end":null,"location":null,"confidence":0.9}]}
            """));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.EnrichCalendarAsync(
                EnabledProfile(), Source(), Consent(EmailIntelligenceOperation.EnrichCalendarCandidates),
                evidence, Now));
    }

    [Fact]
    public async Task CalendarEnrichmentRejectsDuplicateEvidence()
    {
        var evidence = CalendarEvidenceScanner.Scan(new CalendarScanInput(
            "message-1", "Meeting", "Meet 2026-09-15 14:30.", Now)).Suggestions;
        var evidenceId = evidence[0].EvidenceId;
        var coordinator = new EmailIntelligenceCoordinator(new RecordingTransport($$"""
            {"suggestions":[
              {"evidenceId":"{{evidenceId}}","title":"Meeting","start":"2026-09-15T14:30:00-05:00","end":null,"location":null,"confidence":0.9},
              {"evidenceId":"{{evidenceId}}","title":"Duplicate","start":"2026-09-15T14:30:00-05:00","end":null,"location":null,"confidence":0.8}]}
            """));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.EnrichCalendarAsync(
                EnabledProfile(), Source(), Consent(EmailIntelligenceOperation.EnrichCalendarCandidates),
                evidence, Now));
    }

    [Fact]
    public async Task CalendarEnrichmentRejectsTimestampWithoutExplicitOffset()
    {
        var evidence = CalendarEvidenceScanner.Scan(new CalendarScanInput(
            "message-1", "Meeting", "Meet 2026-09-15 14:30.", Now)).Suggestions;
        var coordinator = new EmailIntelligenceCoordinator(new RecordingTransport($$"""
            {"suggestions":[{"evidenceId":"{{evidence[0].EvidenceId}}","title":"Meeting","start":"2026-09-15T14:30:00","end":null,"location":null,"confidence":0.9}]}
            """));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.EnrichCalendarAsync(
                EnabledProfile(), Source(), Consent(EmailIntelligenceOperation.EnrichCalendarCandidates),
                evidence, Now));
    }

    [Fact]
    public void CalendarInterchangeAllDayDraftIsTentativeEvidenceBoundAndProviderNeutral()
    {
        var suggestion = Suggestion(
            new DateTime(2026, 12, 25, 0, 0, 0, DateTimeKind.Unspecified),
            allDay: true);

        var document = CalendarInterchangeWriter.Create(suggestion, Now);
        var text = System.Text.Encoding.UTF8.GetString(document.Utf8Bytes);

        Assert.Equal($"NovaEmail-20261225-{suggestion.EvidenceId[..12]}.ics", document.SuggestedFileName);
        Assert.Equal($"{suggestion.EvidenceId}@novaemail.local.invalid", document.Uid);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            document.Utf8Bytes)), document.Sha256);
        Assert.False(document.UsesFloatingLocalTime);
        Assert.True(document.DurationInferred);
        Assert.True(document.RequiresUserConfirmation);
        Assert.Contains("DTSTART;VALUE=DATE:20261225\r\n", text, StringComparison.Ordinal);
        Assert.Contains("DTEND;VALUE=DATE:20261226\r\n", text, StringComparison.Ordinal);
        Assert.Contains("X-NOVAEMAIL-INFERRED-DURATION:P1D\r\n", text, StringComparison.Ordinal);
        Assert.Contains("X-NOVAEMAIL-REVIEW-REQUIRED:TRUE\r\n", text, StringComparison.Ordinal);
        Assert.Contains("STATUS:TENTATIVE\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ATTENDEE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ORGANIZER", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VALARM", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("URL:", text, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("END:VCALENDAR\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CalendarInterchangeTimedDraftUsesFloatingLocalTimeAndInferredHour()
    {
        var document = CalendarInterchangeWriter.Create(
            Suggestion(new DateTime(2026, 9, 15, 14, 30, 0, DateTimeKind.Unspecified)), Now);
        var text = System.Text.Encoding.UTF8.GetString(document.Utf8Bytes);

        Assert.True(document.UsesFloatingLocalTime);
        Assert.Contains("DTSTART:20260915T143000\r\n", text, StringComparison.Ordinal);
        Assert.Contains("DTEND:20260915T153000\r\n", text, StringComparison.Ordinal);
        Assert.Contains("X-NOVAEMAIL-TIME-BASIS:FLOATING-LOCAL\r\n", text, StringComparison.Ordinal);
        Assert.Contains("X-NOVAEMAIL-INFERRED-DURATION:PT1H\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TZID", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalendarInterchangeEscapesUntrustedTextAndPreventsPropertyInjection()
    {
        var suggestion = Suggestion(
            new DateTime(2026, 9, 15, 14, 30, 0, DateTimeKind.Unspecified)) with
        {
            SuggestedTitle = "Review, revise; confirm\\close\r\nATTENDEE:mailto:attacker@example.test",
            EvidenceText = "Line one\r\nORGANIZER:mailto:attacker@example.test",
        };

        var text = System.Text.Encoding.UTF8.GetString(
            CalendarInterchangeWriter.Create(suggestion, Now).Utf8Bytes);

        Assert.Contains(
            "SUMMARY:Review\\, revise\\; confirm\\\\close\\nATTENDEE:mailto:attacker@exa",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\r\nATTENDEE:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\r\nORGANIZER:", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalendarInterchangeFoldsEveryPhysicalLineAtSeventyFiveUtf8Octets()
    {
        var suggestion = Suggestion(
            new DateTime(2026, 9, 15, 14, 30, 0, DateTimeKind.Unspecified)) with
        {
            SuggestedTitle = string.Concat(Enumerable.Repeat("Résumé 📅 東京 ", 30)),
            EvidenceText = string.Concat(Enumerable.Repeat("evidence 📌 ", 60)),
        };

        var text = System.Text.Encoding.UTF8.GetString(
            CalendarInterchangeWriter.Create(suggestion, Now).Utf8Bytes);
        var lines = text.Split("\r\n", StringSplitOptions.None);

        Assert.Contains(lines, line => line.StartsWith(' '));
        Assert.All(lines, line => Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(line), 0, 75));
    }

    [Fact]
    public void CalendarInterchangeRejectsUnboundOrAmbiguousSuggestions()
    {
        var valid = Suggestion(
            new DateTime(2026, 9, 15, 14, 30, 0, DateTimeKind.Unspecified));

        Assert.Throws<InvalidDataException>(() =>
            CalendarInterchangeWriter.Create(valid with { EvidenceId = "not-a-hash" }, Now));
        Assert.Throws<InvalidDataException>(() =>
            CalendarInterchangeWriter.Create(valid with { EvidenceId = null! }, Now));
        Assert.Throws<InvalidDataException>(() =>
            CalendarInterchangeWriter.Create(valid with { RequiresUserConfirmation = false }, Now));
        Assert.Throws<InvalidDataException>(() => CalendarInterchangeWriter.Create(
            valid with { StartLocal = DateTime.SpecifyKind(valid.StartLocal, DateTimeKind.Local) }, Now));
    }

    [Fact]
    public void CalendarInterchangeIsDeterministicForBoundInputs()
    {
        var suggestion = Suggestion(
            new DateTime(2026, 9, 15, 14, 30, 0, DateTimeKind.Unspecified));

        var first = CalendarInterchangeWriter.Create(suggestion, Now);
        var second = CalendarInterchangeWriter.Create(suggestion, Now);

        Assert.Equal(first.SuggestedFileName, second.SuggestedFileName);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Uid, second.Uid);
        Assert.Equal(first.UsesFloatingLocalTime, second.UsesFloatingLocalTime);
        Assert.Equal(first.DurationInferred, second.DurationInferred);
        Assert.Equal(first.RequiresUserConfirmation, second.RequiresUserConfirmation);
        Assert.Equal(first.Utf8Bytes, second.Utf8Bytes);
    }

    private static EmailIntelligenceProfile EnabledProfile() =>
        new(new Uri("https://localhost:9443/"), EmailIntelligenceProfile.DefaultModel, Enabled: true);

    private static EmailIntelligenceSource Source() =>
        new("message-1", "Planning", "sender@example.test", Now, "Meet 2026-09-15 14:30.");

    private static EmailIntelligenceConsent Consent(EmailIntelligenceOperation operation) =>
        new("message-1", operation, Now, AllowMessageContentProcessing: true);

    private static CalendarEvidenceSuggestion Suggestion(DateTime start, bool allDay = false) =>
        new(
            new string('a', 64),
            "September 15, 2026 at 2:30 PM",
            42,
            start,
            allDay,
            "Quarterly planning",
            RequiresUserConfirmation: true,
            AiEnriched: false);

    private sealed class RecordingTransport(string response) : IEmailIntelligenceTransport
    {
        public EmailIntelligenceRequest? LastRequest { get; private set; }

        public ValueTask<string> CompleteJsonAsync(
            EmailIntelligenceRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return ValueTask.FromResult(response);
        }
    }
}
