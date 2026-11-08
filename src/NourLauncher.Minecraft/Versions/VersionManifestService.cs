using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NourLauncher.Core.Models;

namespace NourLauncher.Minecraft.Versions;

public sealed class VersionManifestService(HttpClient httpClient)
{
    public static readonly Uri ManifestUri =
        new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

    public async Task<IReadOnlyList<MinecraftVersion>> GetVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var manifest = await httpClient.GetFromJsonAsync<VersionManifest>(
            ManifestUri, cancellationToken);

        if (manifest?.Versions is null)
            throw new InvalidOperationException("Mojang returned an empty version manifest.");

        return manifest.Versions.Select(version => new MinecraftVersion(
            version.Id, version.Type, version.ReleaseTime,
            new Uri(version.Url), version.Sha1)).ToArray();
    }

    private sealed record VersionManifest(
        [property: JsonPropertyName("versions")] List<ManifestVersion>? Versions);

    private sealed record ManifestVersion(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("releaseTime")] DateTimeOffset ReleaseTime,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("sha1")] string? Sha1);
}