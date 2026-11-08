using System.Text.Json;

namespace NourLauncher.Storage;

/// <summary>Persisted launcher preferences.</summary>
public sealed record LauncherSettings(
    bool AllowOfflineMode = false,
    bool OfflineModeNoticeAccepted = false);

/// <summary>
/// Stores narrowly scoped, non-secret launcher preferences as JSON next to the profiles.
/// Never store credentials here — tokens belong in the OS credential store.
/// </summary>
public sealed class LauncherSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public LauncherSettingsStore(string? rootDirectory = null)
    {
        var root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SkyClient");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "settings.json");
    }

    public async Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return new LauncherSettings();
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<LauncherSettings>(stream, JsonOptions, cancellationToken)
                ?? new LauncherSettings();
        }
        catch (JsonException)
        {
            // A corrupt preferences file must never block the launcher; fall back to defaults.
            return new LauncherSettings();
        }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
