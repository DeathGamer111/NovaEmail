namespace NovaEmail.Rendering.Tests;

public sealed class SafeHtmlRendererTests
{
    [Fact]
    public async Task HostileHtmlIsSanitizedAndRemoteResourcesAreBlocked()
    {
        var renderer = new SafeHtmlRenderer();
        var source =
            """
            <script>alert('x')</script>
            <iframe src="https://tracker.example/frame"></iframe>
            <form action="https://tracker.example/post"><input name="secret"></form>
            <img src="https://tracker.example/pixel.png" onerror="alert(1)">
            <img src="cid:approved-content-id">
            <a href="javascript:alert(1)" target="_blank">bad</a>
            <a href="https://example.test/path" target="_blank">confirmed externally</a>
            <div style="background-image:url(https://tracker.example/bg);color:red;position:fixed">body</div>
            """;

        var result = await renderer.SanitizeAsync(source);

        Assert.DoesNotContain("<script", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<input", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.example", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("position:fixed", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cid:approved-content-id", result.Html, StringComparison.Ordinal);
        Assert.Contains("https://example.test/path", result.Html, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy", result.Html, StringComparison.Ordinal);
        Assert.Equal(2, result.BlockedRemoteResourceCount);
    }

    [Fact]
    public async Task LightTextWithoutItsOriginalBackgroundIsRepairedForReadability()
    {
        var renderer = new SafeHtmlRenderer();

        var result = await renderer.SanitizeAsync(
            "<div style=\"color:#ffffff\">Previously invisible message text</div>");

        Assert.Contains("Previously invisible message text", result.Html, StringComparison.Ordinal);
        Assert.Contains("color:#000000 !important", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color-scheme:only light", result.Html, StringComparison.Ordinal);
        Assert.True(result.ReadableTextCharacterCount > 0);
    }

    [Fact]
    public async Task SafeBackgroundColorIsPreservedWithItsReadableForeground()
    {
        var renderer = new SafeHtmlRenderer();

        var result = await renderer.SanitizeAsync(
            "<table bgcolor=\"#111111\"><tr><td style=\"color:#ffffff\">Dark email text</td></tr></table>");

        Assert.Contains("bgcolor=\"#111111\"", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dark email text", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("color:#000000 !important", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyHtmlShellReportsThatItHasNoReadableText()
    {
        var renderer = new SafeHtmlRenderer();

        var result = await renderer.SanitizeAsync(
            "<html><body><table><tr><td>&nbsp;</td></tr></table></body></html>");

        Assert.Equal(0, result.ReadableTextCharacterCount);
    }

    [Fact]
    public async Task OnlyExplicitContentIdMappingReceivesTokenizedLocalResourceUrl()
    {
        var renderer = new SafeHtmlRenderer();
        var source =
            "<img src=\"cid:approved-content-id\"><img src=\"novaemail-attachment://attacker/resource\">";
        var approvedUri = "novaemail-attachment://resource/0123456789abcdef/0";

        var result = await renderer.SanitizeAsync(
            source,
            embeddedResourceRewrites: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["approved-content-id"] = approvedUri,
            });

        Assert.Contains(approvedUri + "\"", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("cid:", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.BlockedRemoteResourceCount);
    }

    [Fact]
    public async Task OversizedHtmlIsRejectedBeforeParsing()
    {
        var renderer = new SafeHtmlRenderer();
        var source = new string('x', SafeHtmlRenderer.MaximumHtmlCharacters + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => renderer.SanitizeAsync(source));
    }

    [Fact]
    public void ReadableFallbackKeepsMessageTextAndDropsActiveMarkup()
    {
        var source =
            "<html><body><h1>Quarterly review &amp; follow-up</h1>" +
            "<script>stealCredentials()</script><style>.hidden{display:none}</style>" +
            "<p>Please review the attached agenda.</p><p>Meeting starts at 2:30 PM.</p>" +
            "</body></html>";

        var result = SafeHtmlRenderer.CreateReadableTextFallback(source);

        Assert.Contains("Quarterly review & follow-up", result, StringComparison.Ordinal);
        Assert.Contains("Please review the attached agenda.", result, StringComparison.Ordinal);
        Assert.Contains("Meeting starts at 2:30 PM.", result, StringComparison.Ordinal);
        Assert.DoesNotContain("stealCredentials", result, StringComparison.Ordinal);
        Assert.DoesNotContain("display:none", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadableFallbackAcceptsEmptyHtml()
    {
        Assert.Equal(string.Empty, SafeHtmlRenderer.CreateReadableTextFallback(null));
        Assert.Equal(string.Empty, SafeHtmlRenderer.CreateReadableTextFallback("   "));
    }

    [Theory]
    [InlineData("https://example.test", true)]
    [InlineData("http://example.test", true)]
    [InlineData("mailto:user@example.test", true)]
    [InlineData("mailto:?subject=missing-recipient", false)]
    [InlineData("https://user:password@example.test/path", false)]
    [InlineData("https:opaque-path", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("javascript:alert(1)", false)]
    public void NavigationPolicyIsExplicit(string value, bool expected)
    {
        var created = Uri.TryCreate(value, UriKind.Absolute, out var uri);
        Assert.Equal(expected, created && SafeHtmlRenderer.IsApprovedExternalNavigation(uri!));
    }

    [Fact]
    public void OversizedExternalNavigationIsRejected()
    {
        var uri = new Uri("https://example.test/" + new string('a',
            SafeHtmlRenderer.MaximumExternalNavigationCharacters));
        Assert.False(SafeHtmlRenderer.IsApprovedExternalNavigation(uri));
    }
}
