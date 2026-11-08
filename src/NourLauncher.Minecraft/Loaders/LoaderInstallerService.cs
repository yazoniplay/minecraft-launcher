using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using NourLauncher.Minecraft.Installation;

namespace NourLauncher.Minecraft.Loaders;

public sealed record LoaderVersionInfo(string Version, bool Stable);
public sealed record InstalledLoaderProfile(string MinecraftVersion, string Loader, string LoaderVersion, string ProfileId);

/// <summary>Installs official Fabric and Quilt client profiles and their Maven libraries.</summary>
public sealed class LoaderInstallerService
{
    private readonly HttpClient _http;
    private readonly string _root;
    private readonly MinecraftInstallationService _minecraft;

    public LoaderInstallerService(
        MinecraftInstallationService? minecraft = null,
        HttpClient? httpClient = null,
        string? rootDirectory = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SkyClient/0.3 (https://github.com/yazoniplay/minecraft-launcher)");
        _root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyClient");
        _minecraft = minecraft ?? new MinecraftInstallationService(rootDirectory: _root);
    }

    public async Task<IReadOnlyList<LoaderVersionInfo>> GetAvailableVersionsAsync(
        string minecraftVersion, string loader, CancellationToken cancellationToken = default)
    {
        var endpoint = GetMetadataRoot(loader);
        var url = loader.Equals("fabric", StringComparison.OrdinalIgnoreCase)
            ? $"{endpoint}/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}"
            : $"{endpoint}/v3/versions/loader/{Uri.EscapeDataString(minecraftVersion)}";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var result = new List<LoaderVersionInfo>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var loaderNode = item.TryGetProperty("loader", out var nested) ? nested : item;
            if (!loaderNode.TryGetProperty("version", out var version)) continue;
            var stable = !loaderNode.TryGetProperty("stable", out var stableNode) ||
                         stableNode.ValueKind == JsonValueKind.True;
            result.Add(new LoaderVersionInfo(version.GetString() ?? "", stable));
        }
        return result;
    }

    public async Task<InstalledLoaderProfile> InstallAsync(
        string minecraftVersion, string loader, string? loaderVersion = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        loader = loader.ToLowerInvariant();
        if (loader is not ("fabric" or "quilt"))
            throw new NotSupportedException("This installer currently supports Fabric and Quilt. Forge and NeoForge need their dedicated installer processors.");
        if (!_minecraft.IsInstalled(minecraftVersion))
        {
            progress?.Report($"Installing vanilla Minecraft {minecraftVersion} first…");
            await _minecraft.InstallAsync(minecraftVersion, cancellationToken: cancellationToken);
        }

        var available = await GetAvailableVersionsAsync(minecraftVersion, loader, cancellationToken);
        var selected = loaderVersion is null
            ? available.FirstOrDefault(x => x.Stable) ?? available.FirstOrDefault()
            : available.FirstOrDefault(x => x.Version == loaderVersion);
        if (selected is null)
            throw new InvalidOperationException($"No {loader} loader version is available for Minecraft {minecraftVersion}.");

        var endpoint = GetMetadataRoot(loader);
        var profileUrl = loader == "fabric"
            ? $"{endpoint}/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}/{Uri.EscapeDataString(selected.Version)}/profile/json"
            : $"{endpoint}/v3/versions/loader/{Uri.EscapeDataString(minecraftVersion)}/{Uri.EscapeDataString(selected.Version)}/profile/json";

        progress?.Report($"Downloading {loader} {selected.Version} profile…");
        using var profileResponse = await _http.GetAsync(profileUrl, cancellationToken);
        profileResponse.EnsureSuccessStatusCode();
        var profileJson = await profileResponse.Content.ReadAsStringAsync(cancellationToken);
        using var profile = JsonDocument.Parse(profileJson);
        var profileId = profile.RootElement.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(profileId) || profileId.Any(ch =>
            !char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-' and not '.'))
            throw new InvalidDataException("The loader metadata returned an invalid profile ID.");

        if (!profile.RootElement.TryGetProperty("inheritsFrom", out var parent) ||
            !string.Equals(parent.GetString(), minecraftVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The loader profile does not inherit from the selected Minecraft version.");

        var libraries = profile.RootElement.TryGetProperty("libraries", out var librariesNode)
            ? librariesNode.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        var completed = 0;
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAllowedOnWindows(library)) continue;
            var artifact = ResolveArtifact(library);
            if (artifact is null) continue;
            await DownloadVerifiedAsync(artifact.Value.Url, artifact.Value.Path, artifact.Value.Sha1, cancellationToken);
            completed++;
            progress?.Report($"Downloaded {Path.GetFileName(artifact.Value.Path)} ({completed} loader libraries)…");
        }

        var versionDirectory = Path.Combine(_root, "versions", profileId);
        Directory.CreateDirectory(versionDirectory);
        var versionPath = Path.Combine(versionDirectory, profileId + ".json");
        await File.WriteAllTextAsync(versionPath, profileJson, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(versionDirectory, "loader-installed.json"),
            JsonSerializer.Serialize(new { loader, minecraftVersion, loaderVersion = selected.Version, installedAt = DateTimeOffset.UtcNow }),
            cancellationToken);

        progress?.Report($"{loader} {selected.Version} installed for Minecraft {minecraftVersion}.");
        return new InstalledLoaderProfile(minecraftVersion, loader, selected.Version, profileId);
    }

    private string GetMetadataRoot(string loader) => loader switch
    {
        "fabric" => "https://meta.fabricmc.net",
        "quilt" => "https://meta.quiltmc.org",
        _ => throw new NotSupportedException("Only Fabric and Quilt metadata are currently supported.")
    };

    private (Uri Url, string Path, string? Sha1)? ResolveArtifact(JsonElement library)
    {
        if (library.TryGetProperty("downloads", out var downloads) &&
            downloads.TryGetProperty("artifact", out var artifact))
        {
            var url = artifact.TryGetProperty("url", out var urlNode) ? urlNode.GetString() : null;
            var relative = artifact.TryGetProperty("path", out var pathNode) ? pathNode.GetString() : null;
            if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(relative))
                return (RequireHttps(url), SafeCombine(Path.Combine(_root, "libraries"), relative),
                    artifact.TryGetProperty("sha1", out var sha) ? sha.GetString() : null);
        }

        if (!library.TryGetProperty("name", out var nameNode) || nameNode.GetString() is not string coordinate)
            return null;
        var mavenPath = MavenPath(coordinate);
        var baseUrl = library.TryGetProperty("url", out var repoNode) ? repoNode.GetString() : null;
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        return (RequireHttps(baseUrl.TrimEnd('/') + "/" + mavenPath),
            SafeCombine(Path.Combine(_root, "libraries"), mavenPath), null);
    }

    private async Task DownloadVerifiedAsync(Uri uri, string destination, string? expectedSha1, CancellationToken token)
    {
        if (File.Exists(destination) && await MatchesSha1Async(destination, expectedSha1, token)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(target, token);
            if (!await MatchesSha1Async(temporary, expectedSha1, token))
                throw new InvalidDataException($"SHA-1 verification failed for {Path.GetFileName(destination)}.");
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<bool> MatchesSha1Async(string path, string? expected, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, token)).ToLowerInvariant();
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedOnWindows(JsonElement library)
    {
        if (!library.TryGetProperty("rules", out var rules)) return true;
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var matches = true;
            if (rule.TryGetProperty("os", out var os) &&
                os.TryGetProperty("name", out var name) &&
                !string.Equals(name.GetString(), "windows", StringComparison.OrdinalIgnoreCase))
                matches = false;
            if (matches)
                allowed = rule.TryGetProperty("action", out var action) && action.GetString() == "allow";
        }
        return allowed;
    }

    private static string MavenPath(string coordinate)
    {
        var parts = coordinate.Split(':');
        if (parts.Length < 3) throw new InvalidDataException($"Invalid Maven coordinate: {coordinate}");
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        var extension = "jar";
        if (version.Contains('@'))
        {
            var split = version.Split('@', 2);
            version = split[0];
            extension = split[1];
        }
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{extension}";
    }

    private static Uri RequireHttps(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("A loader metadata URL was not HTTPS.");
        return uri;
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A loader library path escaped the managed library directory.");
        return fullPath;
    }
}
