using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace NovaEmail.Rendering;

public sealed record SafeHtmlDocument(
    string Html,
    int BlockedRemoteResourceCount,
    int ReadableTextCharacterCount,
    TimeSpan ProcessingTime);

public sealed class SafeHtmlRenderer
{
    private readonly record struct CssColor(byte Red, byte Green, byte Blue, double Alpha = 1);

    public const int MaximumHtmlCharacters = 16 * 1024 * 1024;
    public const int MaximumExternalNavigationCharacters = 4 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim SanitizerGate = new(1, 1);
    private readonly TimeSpan _defaultTimeout;

    private static readonly string[] AllowedTags =
    [
        "a", "abbr", "address", "article", "aside", "b", "bdi", "blockquote", "br",
        "caption", "center", "cite", "code", "col", "colgroup", "dd", "del", "details",
        "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "footer", "h1", "h2",
        "h3", "h4", "h5", "h6", "header", "hr", "i", "img", "ins", "kbd", "li",
        "main", "mark", "ol", "p", "pre", "q", "s", "samp", "section", "small", "span",
        "strike", "strong", "sub", "summary", "sup", "table", "tbody", "td", "tfoot", "th",
        "thead", "time", "tr", "tt", "u", "ul", "var", "wbr",
    ];

    private static readonly string[] AllowedAttributes =
    [
        "abbr", "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "char", "charoff",
        "cite", "clear", "color", "colspan", "datetime", "dir", "headers", "height", "href",
        "hreflang", "lang", "name", "nowrap", "rel", "rowspan", "scope", "span", "src",
        "style", "title", "valign", "width",
    ];

    private static readonly string[] AllowedCssProperties =
    [
        "background-color", "border", "border-bottom", "border-collapse", "border-color",
        "border-left", "border-right", "border-spacing", "border-style", "border-top", "border-width",
        "color", "direction", "display", "font", "font-family", "font-size", "font-style",
        "font-variant", "font-weight", "height", "letter-spacing", "line-height", "list-style",
        "margin", "margin-bottom", "margin-left", "margin-right", "margin-top", "max-width",
        "min-width", "overflow-wrap", "padding", "padding-bottom", "padding-left", "padding-right",
        "padding-top", "text-align", "text-decoration", "text-indent", "text-transform",
        "vertical-align", "white-space", "width", "word-break", "word-spacing",
    ];

    public SafeHtmlRenderer(TimeSpan? defaultTimeout = null)
    {
        _defaultTimeout = defaultTimeout ?? DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_defaultTimeout, TimeSpan.Zero);
    }

    public async Task<SafeHtmlDocument> SanitizeAsync(
        string untrustedHtml,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? embeddedResourceRewrites = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(untrustedHtml);
        if (untrustedHtml.Length > MaximumHtmlCharacters)
        {
            throw new InvalidDataException("HTML message exceeds the configured rendering limit.");
        }
        var effectiveTimeout = timeout ?? _defaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveTimeout, TimeSpan.Zero);
        var approvedRewrites = ValidateEmbeddedResourceRewrites(embeddedResourceRewrites);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(effectiveTimeout);
        var gateAcquired = false;
        try
        {
            await SanitizerGate.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            gateAcquired = true;
            var sanitizeTask = Task.Run(() =>
            {
                try
                {
                    return SanitizeCore(untrustedHtml, approvedRewrites);
                }
                finally
                {
                    SanitizerGate.Release();
                }
            }, CancellationToken.None);
            gateAcquired = false;
            return await sanitizeTask.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("HTML sanitization exceeded its processing-time limit.", exception);
        }
        finally
        {
            if (gateAcquired) SanitizerGate.Release();
        }
    }

    public static string CreateReadableTextFallback(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var wasTruncated = html.Length > MaximumHtmlCharacters;
        var bounded = wasTruncated ? html[..MaximumHtmlCharacters] : html;
        try
        {
            var document = new HtmlParser().ParseDocument(bounded);
            foreach (var hidden in document.QuerySelectorAll(
                         "script,style,noscript,template,svg,canvas,object,embed,iframe"))
                hidden.Remove();
            foreach (var breakElement in document.QuerySelectorAll(
                         "br,p,div,li,tr,h1,h2,h3,h4,h5,h6,blockquote,pre"))
                breakElement.AppendChild(document.CreateTextNode(Environment.NewLine));
            var text = document.Body?.TextContent ?? document.DocumentElement?.TextContent ?? bounded;
            text = WebUtility.HtmlDecode(text).Trim();
            if (wasTruncated)
                text += Environment.NewLine + Environment.NewLine +
                    "[The display fallback reached its local size limit; the original copied message remains unchanged.]";
            return text;
        }
        catch
        {
            return bounded + (wasTruncated
                ? Environment.NewLine + Environment.NewLine +
                  "[The display fallback reached its local size limit; the original copied message remains unchanged.]"
                : string.Empty);
        }
    }

    public static bool IsApprovedExternalNavigation(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            uri.OriginalString.Length is 0 or > MaximumExternalNavigationCharacters ||
            uri.OriginalString.Any(char.IsControl) ||
            uri.Scheme is not ("https" or "http" or "mailto"))
            return false;
        if (uri.Scheme is "https" or "http")
            return !string.IsNullOrWhiteSpace(uri.IdnHost) &&
                string.IsNullOrEmpty(uri.UserInfo);
        try
        {
            var decoded = Uri.UnescapeDataString(uri.OriginalString);
            if (decoded.Any(char.IsControl)) return false;
            var separator = decoded.IndexOf(':');
            if (separator < 0 || separator == decoded.Length - 1) return false;
            var target = decoded.AsSpan(separator + 1);
            var query = target.IndexOf('?');
            var recipient = query < 0 ? target : target[..query];
            return !recipient.IsEmpty && !recipient.StartsWith("//", StringComparison.Ordinal) &&
                !recipient.ContainsAny(" \t") && recipient.Contains('@');
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static SafeHtmlDocument SanitizeCore(
        string untrustedHtml,
        Dictionary<string, string> embeddedResourceRewrites)
    {
        var stopwatch = Stopwatch.StartNew();
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedTags.UnionWith(AllowedTags);
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.UnionWith(AllowedAttributes);
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedCssProperties.UnionWith(AllowedCssProperties);
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.UnionWith(["cid", "novaemail-attachment", "https", "http", "mailto"]);

        var blockedRemoteResources = 0;
        var readableTextCharacterCount = 0;
        sanitizer.PostProcessDom += (_, eventArgs) =>
        {
            foreach (var image in eventArgs.Document.QuerySelectorAll("img"))
            {
                var source = image.GetAttribute("src");
                if (TryNormalizeCidSource(source, out var contentId) &&
                    embeddedResourceRewrites.TryGetValue(contentId, out var replacement))
                {
                    image.SetAttribute("src", replacement);
                }
                else
                {
                    image.RemoveAttribute("src");
                    image.SetAttribute("data-novaemail-remote-blocked", "true");
                    image.SetAttribute("alt", string.IsNullOrWhiteSpace(image.GetAttribute("alt"))
                        ? "Remote image blocked"
                        : image.GetAttribute("alt")! + " (remote image blocked)");
                    blockedRemoteResources++;
                }
            }

            foreach (var anchor in eventArgs.Document.QuerySelectorAll("a"))
            {
                anchor.RemoveAttribute("target");
                anchor.SetAttribute("rel", "noopener noreferrer nofollow");
                var href = anchor.GetAttribute("href");
                if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || !IsApprovedExternalNavigation(uri))
                {
                    anchor.RemoveAttribute("href");
                }
            }

            RepairLowContrastText(eventArgs.Document.Body);
            readableTextCharacterCount = eventArgs.Document.Body?.TextContent.Count(
                character => !char.IsWhiteSpace(character)) ?? 0;
        };

        var body = sanitizer.Sanitize(untrustedHtml);
        stopwatch.Stop();
        return new SafeHtmlDocument(
            BuildDocument(body), blockedRemoteResources, readableTextCharacterCount, stopwatch.Elapsed);
    }

    private static Dictionary<string, string> ValidateEmbeddedResourceRewrites(
        IReadOnlyDictionary<string, string>? rewrites)
    {
        if (rewrites is null || rewrites.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rewrites.Count > 2_048)
            throw new InvalidDataException("Embedded-resource rewrite count exceeds its safety limit.");
        var validated = new Dictionary<string, string>(rewrites.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var rewrite in rewrites)
        {
            var contentId = NormalizeContentId(rewrite.Key);
            if (contentId.Length == 0 ||
                !Uri.TryCreate(rewrite.Value, UriKind.Absolute, out var replacement) ||
                !replacement.Scheme.Equals("novaemail-attachment", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Embedded-resource rewrite is invalid.");
            if (!validated.TryAdd(contentId, replacement.AbsoluteUri))
                throw new InvalidDataException("Embedded-resource rewrite contains a duplicate Content-ID.");
        }
        return validated;
    }

    private static bool TryNormalizeCidSource(string? source, out string contentId)
    {
        contentId = string.Empty;
        if (string.IsNullOrWhiteSpace(source) ||
            !source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            contentId = NormalizeContentId(Uri.UnescapeDataString(source[4..]));
            return contentId.Length > 0;
        }
        catch (Exception exception) when (exception is UriFormatException or InvalidDataException)
        {
            return false;
        }
    }

    private static string NormalizeContentId(string contentId)
    {
        var normalized = contentId.Trim();
        if (normalized.Length >= 2 && normalized[0] == '<' && normalized[^1] == '>')
            normalized = normalized[1..^1].Trim();
        if (normalized.Length > 512 || normalized.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character) || character is '<' or '>'))
            throw new InvalidDataException("Embedded Content-ID is invalid or exceeds its safety limit.");
        return normalized;
    }

    private static void RepairLowContrastText(IElement? body)
    {
        if (body is null) return;
        RepairLowContrastText(
            body,
            inheritedForeground: new CssColor(32, 33, 36),
            inheritedBackground: new CssColor(255, 255, 255));
    }

    private static void RepairLowContrastText(
        IElement element,
        CssColor inheritedForeground,
        CssColor inheritedBackground)
    {
        var background = TryGetDeclaredColor(element, "background-color", "bgcolor", out var declaredBackground)
            ? Composite(declaredBackground, inheritedBackground)
            : inheritedBackground;
        var foreground = TryGetDeclaredColor(element, "color", "color", out var declaredForeground)
            ? Composite(declaredForeground, background)
            : inheritedForeground;

        var containsDirectText = element.ChildNodes.Any(node =>
            node.NodeType == NodeType.Text && !string.IsNullOrWhiteSpace(node.TextContent));
        if (containsDirectText && ContrastRatio(foreground, background) < 4.5)
        {
            var black = new CssColor(0, 0, 0);
            var white = new CssColor(255, 255, 255);
            foreground = ContrastRatio(black, background) >= ContrastRatio(white, background)
                ? black
                : white;
            var existingStyle = element.GetAttribute("style")?.Trim().TrimEnd(';');
            var separator = string.IsNullOrWhiteSpace(existingStyle) ? string.Empty : ";";
            element.SetAttribute(
                "style",
                $"{existingStyle}{separator}color:#{foreground.Red:x2}{foreground.Green:x2}{foreground.Blue:x2} !important");
        }

        foreach (var child in element.Children)
            RepairLowContrastText(child, foreground, background);
    }

    private static bool TryGetDeclaredColor(
        IElement element,
        string cssProperty,
        string fallbackAttribute,
        out CssColor color)
    {
        if (TryGetStyleValue(element.GetAttribute("style"), cssProperty, out var styleValue) &&
            TryParseCssColor(styleValue, out color))
            return true;
        return TryParseCssColor(element.GetAttribute(fallbackAttribute), out color);
    }

    private static bool TryGetStyleValue(string? style, string property, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(style)) return false;
        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = declaration.IndexOf(':');
            if (separator <= 0 ||
                !declaration[..separator].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
                continue;
            value = declaration[(separator + 1)..]
                .Replace("!important", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
        }
        return value.Length > 0;
    }

    private static bool TryParseCssColor(string? value, out CssColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Trim();
        if (candidate.StartsWith('#'))
            return TryParseHexColor(candidate.AsSpan(1), out color);
        if (candidate.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) && candidate.EndsWith(')'))
            return TryParseFunctionalColor(candidate.AsSpan(4, candidate.Length - 5), false, out color);
        if (candidate.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) && candidate.EndsWith(')'))
            return TryParseFunctionalColor(candidate.AsSpan(5, candidate.Length - 6), true, out color);

        color = candidate.ToLowerInvariant() switch
        {
            "black" => new CssColor(0, 0, 0),
            "silver" => new CssColor(192, 192, 192),
            "gray" or "grey" => new CssColor(128, 128, 128),
            "white" => new CssColor(255, 255, 255),
            "maroon" => new CssColor(128, 0, 0),
            "red" => new CssColor(255, 0, 0),
            "purple" => new CssColor(128, 0, 128),
            "fuchsia" or "magenta" => new CssColor(255, 0, 255),
            "green" => new CssColor(0, 128, 0),
            "lime" => new CssColor(0, 255, 0),
            "olive" => new CssColor(128, 128, 0),
            "yellow" => new CssColor(255, 255, 0),
            "navy" => new CssColor(0, 0, 128),
            "blue" => new CssColor(0, 0, 255),
            "teal" => new CssColor(0, 128, 128),
            "aqua" or "cyan" => new CssColor(0, 255, 255),
            "orange" => new CssColor(255, 165, 0),
            "transparent" => new CssColor(0, 0, 0, 0),
            _ => default,
        };
        return candidate.Equals("black", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("silver", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("gray", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("grey", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("white", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("maroon", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("red", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("purple", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("fuchsia", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("magenta", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("green", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("lime", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("olive", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("yellow", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("navy", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("blue", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("teal", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("aqua", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("cyan", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("orange", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("transparent", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseHexColor(ReadOnlySpan<char> value, out CssColor color)
    {
        color = default;
        if (value.Length is not (3 or 4 or 6 or 8)) return false;
        Span<byte> components = stackalloc byte[4];
        components[3] = 255;
        if (value.Length is 3 or 4)
        {
            for (var index = 0; index < value.Length; index++)
            {
                if (!byte.TryParse(value.Slice(index, 1), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var nibble))
                    return false;
                components[index] = (byte)(nibble * 17);
            }
        }
        else
        {
            for (var index = 0; index < value.Length / 2; index++)
            {
                if (!byte.TryParse(value.Slice(index * 2, 2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out components[index]))
                    return false;
            }
        }
        color = new CssColor(components[0], components[1], components[2], components[3] / 255d);
        return true;
    }

    private static bool TryParseFunctionalColor(
        ReadOnlySpan<char> value,
        bool includesAlpha,
        out CssColor color)
    {
        color = default;
        var parts = value.ToString().Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != (includesAlpha ? 4 : 3)) return false;
        Span<byte> channels = stackalloc byte[3];
        for (var index = 0; index < channels.Length; index++)
        {
            if (parts[index].EndsWith('%'))
            {
                if (!double.TryParse(parts[index][..^1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var percent) || percent is < 0 or > 100)
                    return false;
                channels[index] = (byte)Math.Round(percent * 2.55);
            }
            else if (!byte.TryParse(parts[index], NumberStyles.Integer,
                         CultureInfo.InvariantCulture, out channels[index]))
                return false;
        }
        var alpha = 1d;
        if (includesAlpha && (!double.TryParse(parts[3], NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out alpha) || alpha is < 0 or > 1))
            return false;
        color = new CssColor(channels[0], channels[1], channels[2], alpha);
        return true;
    }

    private static CssColor Composite(CssColor foreground, CssColor background)
    {
        if (foreground.Alpha >= 1) return foreground with { Alpha = 1 };
        if (foreground.Alpha <= 0) return background;
        byte Blend(byte front, byte back) =>
            (byte)Math.Round(front * foreground.Alpha + back * (1 - foreground.Alpha));
        return new CssColor(
            Blend(foreground.Red, background.Red),
            Blend(foreground.Green, background.Green),
            Blend(foreground.Blue, background.Blue));
    }

    private static double ContrastRatio(CssColor first, CssColor second)
    {
        static double Luminance(CssColor color)
        {
            static double Channel(byte value)
            {
                var normalized = value / 255d;
                return normalized <= 0.04045
                    ? normalized / 12.92
                    : Math.Pow((normalized + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color.Red) +
                0.7152 * Channel(color.Green) +
                0.0722 * Channel(color.Blue);
        }

        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
            (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static string BuildDocument(string sanitizedBody)
    {
        const string contentSecurityPolicy =
            "default-src 'none'; base-uri 'none'; connect-src 'none'; font-src 'none'; " +
            "form-action 'none'; frame-ancestors 'none'; frame-src 'none'; img-src novaemail-attachment:; " +
            "media-src 'none'; object-src 'none'; script-src 'none'; style-src 'unsafe-inline'";
        var builder = new StringBuilder(sanitizedBody.Length + 1024);
        builder.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        builder.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"");
        builder.Append(WebUtility.HtmlEncode(contentSecurityPolicy));
        builder.Append("\"><style>html{color-scheme:only light;background:#fff;color:#202124}body{font:1rem/1.45 'Segoe UI',sans-serif;margin:16px;overflow-wrap:anywhere;background:#fff;color:#202124}img{max-width:100%;height:auto}[data-novaemail-remote-blocked]{display:inline-block;border:1px solid currentColor;padding:4px}table{max-width:100%;border-collapse:collapse}a{cursor:pointer}</style></head><body>");
        builder.Append(sanitizedBody);
        builder.Append("</body></html>");
        return builder.ToString();
    }
}
