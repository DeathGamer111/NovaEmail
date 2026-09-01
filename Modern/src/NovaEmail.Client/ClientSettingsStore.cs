using System.Text.Json;
using NovaEmail.Intelligence;
using NovaEmail.Safety;

namespace NovaEmail.Client;

internal sealed record ClientSettings(
    string SenderAddress,
    string AiEndpoint,
    string AiModel)
{
    public static ClientSettings Default { get; } = new(
        "demo@novaemail.local",
        EmailIntelligenceProfile.DisabledLocalDefault().Endpoint.AbsoluteUri,
        EmailIntelligenceProfile.DefaultModel);
}

internal static class ClientSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static string SettingsPath => Path.Combine(ApplicationIdentity.SettingsRoot, "client.json");

    public static async Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath)) return ClientSettings.Default;
        await using var input = File.OpenRead(SettingsPath);
        return await JsonSerializer.DeserializeAsync<ClientSettings>(input, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? ClientSettings.Default;
    }

    public static async Task SaveAsync(ClientSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(ApplicationIdentity.SettingsRoot);
        var temporaryPath = SettingsPath + ".new";
        await using (var output = new FileStream(
                         temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 16_384, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(output, settings, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
