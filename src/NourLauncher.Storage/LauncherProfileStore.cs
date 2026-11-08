using System.Text.Json;
using System.Text.Json.Serialization;
using NourLauncher.Core.Models;

namespace NourLauncher.Storage;

public sealed class LauncherProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public LauncherProfileStore(string? rootDirectory = null)
    {
        var root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SkyClient");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "profiles.json");
    }

    public async Task<IReadOnlyList<LauncherProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return Array.Empty<LauncherProfile>();
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<LauncherProfile>>(stream, JsonOptions, cancellationToken)
            ?? new List<LauncherProfile>();
    }

    public async Task SaveAsync(IEnumerable<LauncherProfile> profiles, CancellationToken cancellationToken = default)
    {
        var temporaryPath = _filePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, profiles, JsonOptions, cancellationToken);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
