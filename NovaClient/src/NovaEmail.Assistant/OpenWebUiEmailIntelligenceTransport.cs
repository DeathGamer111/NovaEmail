using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaEmail.Intelligence;
using NovaEmail.Safety;
using SecurityException = System.Security.SecurityException;

namespace NovaEmail.Assistant;

public enum OpenWebUiServiceFailure
{
    AuthenticationRejected,
    EndpointNotFound,
    RateLimited,
    ServiceUnavailable,
    RequestRejected,
}

public sealed class OpenWebUiServiceException(
    OpenWebUiServiceFailure failure,
    string message) : Exception(message)
{
    public OpenWebUiServiceFailure Failure { get; } = failure;
}

/// <summary>
/// A bounded OpenWebUI chat-completions adapter for Nova Email.
/// It has no tool, file, retrieval, chat-persistence, or mail mutation capability.
/// </summary>
public sealed class OpenWebUiEmailIntelligenceTransport : IEmailIntelligenceTransport, IDisposable
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    public const string CompletionPath = "ollama/api/chat";
    public const int MaximumRequestUtf8Bytes = 1_200_000;
    public const int MaximumResponseUtf8Bytes = 196_608;
    public const int MaximumCompletionTokens = 4_096;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly HttpMessageInvoker _client;
    private readonly X509Certificate2Collection _customTrustRoots;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    public OpenWebUiEmailIntelligenceTransport(
        AssistantEndpointPolicy assistantEndpointPolicy,
        string apiKey,
        IEnumerable<X509Certificate2>? customTrustRoots = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(assistantEndpointPolicy);
        ValidateApiKey(apiKey);
        var boundedTimeout = timeout ?? DefaultTimeout;
        ValidateTimeout(boundedTimeout);
        _customTrustRoots = CloneTrustRoots(customTrustRoots);
        try
        {
            _client = new HttpMessageInvoker(
                CreateHandler(assistantEndpointPolicy, _customTrustRoots),
                disposeHandler: true);
        }
        catch
        {
            DisposeTrustRoots(_customTrustRoots);
            throw;
        }
        _apiKey = apiKey;
        _timeout = boundedTimeout;
    }

    internal OpenWebUiEmailIntelligenceTransport(
        HttpMessageHandler handler,
        string apiKey,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ValidateApiKey(apiKey);
        ValidateTimeout(timeout);
        _client = new HttpMessageInvoker(handler, disposeHandler: true);
        _customTrustRoots = [];
        _apiKey = apiKey;
        _timeout = timeout;
    }

    public async ValueTask<string> CompleteJsonAsync(
        EmailIntelligenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRequest(request);

        var target = new Uri(request.Profile.Endpoint, CompletionPath);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new CompletionRequest(
                request.Profile.Model,
                Stream: false,
                Think: false,
                Format: "json",
                new CompletionOptions(Temperature: 0.2, MaximumCompletionTokens),
                [
                    new CompletionMessage("system", request.SystemInstruction),
                    new CompletionMessage("user", request.InputJson),
                ]),
            WireJson);
        if (payload.Length > MaximumRequestUtf8Bytes)
            throw new InvalidDataException("The AI HTTP request exceeds its safety limit.");

        using var message = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(payload),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.ConnectionClose = true;
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(message, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The local AI request exceeded its time limit.", exception);
        }

        using (response)
        {
            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
                throw CreateServiceException(response.StatusCode);
            ValidateContentType(response.Content.Headers.ContentType);
            var body = await ReadBoundedBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return ParseCompletion(
                body, request.MaximumOutputCharacters, request.Profile.Model);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        DisposeTrustRoots(_customTrustRoots);
    }

    private static SocketsHttpHandler CreateHandler(
        AssistantEndpointPolicy assistantEndpointPolicy,
        X509Certificate2Collection customTrustRoots)
    {
        var sslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = EndpointPolicy.AllowedTlsProtocols,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };
        if (customTrustRoots.Count > 0)
        {
            sslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                ValidateCertificate(certificate, errors, customTrustRoots);
        }
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 1,
            MaxResponseHeadersLength = 32,
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            UseCookies = false,
            UseProxy = false,
            SslOptions = sslOptions,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var uri = context.InitialRequestMessage.RequestUri ??
                    throw new SecurityException("The AI request URI is missing.");
                var expectedPort = uri.IsDefaultPort ? 443 : uri.Port;
                if (!string.Equals(context.DnsEndPoint.Host, uri.DnsSafeHost,
                        StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != expectedPort)
                    throw new SecurityException("The AI socket target does not match the authorized request URI.");
                var socket = await assistantEndpointPolicy.ConnectAllowedAsync(
                    uri.DnsSafeHost, expectedPort, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
    }

    private static void ValidateRequest(EmailIntelligenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Profile.Validate();
        if (!request.Profile.Enabled)
            throw new InvalidOperationException("AI processing is disabled.");
        if (string.IsNullOrWhiteSpace(request.SystemInstruction) ||
            request.SystemInstruction.Length > 4_096 ||
            request.SystemInstruction.Any(character => char.IsControl(character) &&
                character is not '\r' and not '\n' and not '\t'))
            throw new InvalidDataException("The AI system instruction is invalid.");
        if (string.IsNullOrWhiteSpace(request.InputJson) ||
            Encoding.UTF8.GetByteCount(request.InputJson) > EmailIntelligenceCoordinator.MaximumInputJsonUtf8Bytes)
            throw new InvalidDataException("The AI input is empty or exceeds its safety limit.");
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.InputJson)));
        if (!string.Equals(actualHash, request.InputSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The AI input hash does not match the authorized request.");
        if (request.MaximumOutputCharacters is < 1 or > EmailIntelligenceCoordinator.MaximumOutputCharacters)
            throw new InvalidDataException("The AI output limit is invalid.");
    }

    private static void ValidateApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (apiKey.Length > 256 || apiKey.Any(char.IsControl) || apiKey.Any(char.IsWhiteSpace))
            throw new ArgumentException("The AI API key is invalid.", nameof(apiKey));
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.FromMilliseconds(10) || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    private static X509Certificate2Collection CloneTrustRoots(
        IEnumerable<X509Certificate2>? customTrustRoots)
    {
        var result = new X509Certificate2Collection();
        if (customTrustRoots is null) return result;
        try
        {
            foreach (var root in customTrustRoots)
            {
                ArgumentNullException.ThrowIfNull(root);
                if (result.Count == 8)
                    throw new ArgumentOutOfRangeException(
                        nameof(customTrustRoots), "At most eight local trust roots may be enrolled.");
                var clone = X509CertificateLoader.LoadCertificate(root.RawData);
                try
                {
                    ValidateTrustRoot(clone);
                    result.Add(clone);
                }
                catch
                {
                    clone.Dispose();
                    throw;
                }
            }
            return result;
        }
        catch
        {
            DisposeTrustRoots(result);
            throw;
        }
    }

    private static bool ValidateCertificate(
        X509Certificate? certificate,
        SslPolicyErrors errors,
        X509Certificate2Collection customTrustRoots)
    {
        if (certificate is null ||
            errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ||
            errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return false;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(customTrustRoots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthenticationOid));
        return chain.Build(leaf);
    }

    private static void ValidateTrustRoot(X509Certificate2 root)
    {
        var basicConstraints = root.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .SingleOrDefault();
        var now = DateTimeOffset.UtcNow;
        if (basicConstraints?.CertificateAuthority is not true ||
            now < root.NotBefore.ToUniversalTime() || now > root.NotAfter.ToUniversalTime())
            throw new SecurityException("The local AI trust input is not a currently valid certificate authority.");
        using var rsa = root.GetRSAPublicKey();
        using var ecdsa = root.GetECDsaPublicKey();
        if (rsa is not null && rsa.KeySize < 2048 ||
            ecdsa is not null && ecdsa.KeySize < 256 ||
            rsa is null && ecdsa is null)
            throw new SecurityException("The local AI certificate authority uses an unsupported public key.");
    }

    private static void DisposeTrustRoots(X509Certificate2Collection trustRoots)
    {
        foreach (var certificate in trustRoots) certificate.Dispose();
        trustRoots.Clear();
    }

    private static void ValidateContentType(MediaTypeHeaderValue? contentType)
    {
        if (contentType is null ||
            !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(contentType.CharSet) &&
             !string.Equals(contentType.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The local AI response is not UTF-8 JSON.");
    }

    private static OpenWebUiServiceException CreateServiceException(HttpStatusCode statusCode)
    {
        var failure = statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                OpenWebUiServiceFailure.AuthenticationRejected,
            HttpStatusCode.NotFound => OpenWebUiServiceFailure.EndpointNotFound,
            HttpStatusCode.TooManyRequests => OpenWebUiServiceFailure.RateLimited,
            _ when (int)statusCode >= 500 => OpenWebUiServiceFailure.ServiceUnavailable,
            _ => OpenWebUiServiceFailure.RequestRejected,
        };
        return new OpenWebUiServiceException(
            failure,
            $"The AI service returned HTTP {(int)statusCode}; its response body was discarded.");
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseUtf8Bytes)
            throw new InvalidDataException("The local AI response exceeds its safety limit.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8_192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseUtf8Bytes)
                throw new InvalidDataException("The local AI response exceeds its safety limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string ParseCompletion(
        byte[] responseBytes,
        int maximumOutputCharacters,
        string expectedModel)
    {
        CompletionResponse response;
        try
        {
            var json = StrictUtf8.GetString(responseBytes);
            response = JsonSerializer.Deserialize<CompletionResponse>(json, WireJson) ??
                throw new InvalidDataException("The local AI response is empty.");
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The local AI response contains invalid UTF-8.", exception);
        }
        if (!string.Equals(response.Model, expectedModel, StringComparison.Ordinal) ||
            response.Done is not true ||
            !string.Equals(response.DoneReason, "stop", StringComparison.Ordinal))
            throw new InvalidDataException("The local AI response is not a completed result from the requested model.");
        var responseMessage = response.Message;
        if (responseMessage is null ||
            !string.Equals(responseMessage.Role, "assistant", StringComparison.Ordinal) ||
            responseMessage.ToolCalls is not null)
            throw new SecurityException("The local AI response contained a prohibited message shape or tool call.");

        // Some Qwen builds exposed through OpenWebUI place JSON-mode output in
        // `thinking` while leaving `content` empty. Either field remains
        // untrusted; the coordinator still applies the same strict operation-
        // specific JSON contract before anything is displayed or inserted.
        var content = string.IsNullOrWhiteSpace(responseMessage.Content)
            ? responseMessage.Thinking
            : responseMessage.Content;
        if (string.IsNullOrWhiteSpace(content) || content.Length > maximumOutputCharacters)
            throw new InvalidDataException("The local AI response content is empty or exceeds its safety limit.");
        return content;
    }

    private sealed record CompletionRequest(
        string Model,
        bool Stream,
        bool Think,
        string Format,
        CompletionOptions Options,
        IReadOnlyList<CompletionMessage> Messages);

    private sealed record CompletionOptions(
        double Temperature,
        [property: JsonPropertyName("num_predict")] int MaximumCompletionTokens);

    private sealed record CompletionMessage(string Role, string Content);

    private sealed record CompletionResponse(
        string? Model,
        [property: JsonPropertyName("created_at")] string? CreatedAt,
        CompletionResponseMessage? Message,
        bool? Done,
        [property: JsonPropertyName("done_reason")] string? DoneReason,
        [property: JsonPropertyName("total_duration")] long? TotalDuration,
        [property: JsonPropertyName("load_duration")] long? LoadDuration,
        [property: JsonPropertyName("prompt_eval_count")] long? PromptEvaluationCount,
        [property: JsonPropertyName("prompt_eval_duration")] long? PromptEvaluationDuration,
        [property: JsonPropertyName("eval_count")] long? EvaluationCount,
        [property: JsonPropertyName("eval_duration")] long? EvaluationDuration);

    private sealed record CompletionResponseMessage(
        string? Role,
        string? Content,
        string? Thinking,
        [property: JsonPropertyName("tool_calls")] JsonElement? ToolCalls);
}
