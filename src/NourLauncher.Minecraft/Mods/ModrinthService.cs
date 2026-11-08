using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace NourLauncher.Minecraft.Mods;

public sealed record ModrinthProject(
    string Id, string Slug, string Title, string Description, string Author,
    string ProjectType, long Downloads, string? IconUrl);

public sealed record ModrinthVersion(
    string Id, string Name, string VersionNumber, string VersionType,
    IReadOnlyList<string> GameVersions, IReadOnlyList<string> Loaders,
    IReadOnlyList<ModrinthFile> Files);

public sealed record ModrinthFile(
    string Filename, string Url, bool Primary, string? Sha512, long Size);

/// <summary>Modrinth API client for searching, resolving compatible versions and installing mods.</summary>
public sealed class ModrinthService
{
    private readonly HttpClient _http;
    private readonly string _root;

    public ModrinthService(HttpClient? httpClient = null, string? rootDirectory = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SkyClient/0.3 (https://github.com/yazoniplay/minecraft-launcher)");
        _root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyClient");
    }

    public async Task<IReadOnlyList<ModrinthProject>> SearchModsAsync(
        string query, string minecraftVersion, string loader, int limit = 30,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Enter a mod name to search.", nameof(query));
        var facets = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new[] { "project_type:mod" },
            new[] { "versions:" + minecraftVersion },
            new[] { "categories:" + loader.ToLowerInvariant() }
        });
        var url = "https://api.modrinth.com/v2/search?query=" + Uri.EscapeDataString(query.Trim()) +
                  "&facets=" + Uri.EscapeDataString(facets) + "&limit=" + Math.Clamp(limit, 1, 100);
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var results = new List<ModrinthProject>();
        foreach (var item in document.RootElement.GetProperty("hits").EnumerateArray())
        {
            results.Add(new ModrinthProject(
                item.GetProperty("project_id").GetString()!,
                item.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
                item.GetProperty("title").GetString() ?? "Unknown mod",
                item.GetProperty("description").GetString() ?? "",
                item.GetProperty("author").GetString() ?? "Unknown",
                item.GetProperty("project_type").GetString() ?? "mod",
                item.TryGetProperty("downloads", out var downloads) ? downloads.GetInt64() : 0,
                item.TryGetProperty("icon_url", out var icon) ? icon.GetString() : null));
        }
        return results;
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetCompatibleVersionsAsync(
        string projectId, string minecraftVersion, string loader,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId)}/version" +
                  "?game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraftVersion })) +
                  "&loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader.ToLowerInvariant() }));
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var versions = new List<ModrinthVersion>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var gameVersions = item.GetProperty("game_versions").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            var loaders = item.GetProperty("loaders").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            var files = item.GetProperty("files").EnumerateArray().Select(file => new ModrinthFile(
                file.GetProperty("filename").GetString()!,
                file.GetProperty("url").GetString()!,
                file.TryGetProperty("primary", out var primary) && primary.GetBoolean(),
                file.TryGetProperty("hashes", out var hashes) && hashes.TryGetProperty("sha512", out var sha) ? sha.GetString() : null,
                file.TryGetProperty("size", out var size) ? size.GetInt64() : 0)).ToArray();
            versions.Add(new ModrinthVersion(
                item.GetProperty("id").GetString()!,
                item.GetProperty("name").GetString() ?? "",
                item.GetProperty("version_number").GetString() ?? "",
                item.GetProperty("version_type").GetString() ?? "release",
                gameVersions, loaders, files));
        }
        return versions;
    }

    public async Task<string> InstallVersionAsync(
        ModrinthVersion version, string instanceName,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        var instanceDirectory = GetInstanceDirectory(instanceName);
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
        if (file is null) throw new InvalidDataException("This Modrinth version has no downloadable file.");
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Modrinth returned an unexpected download host.");
        if (!file.Filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            file.Filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            file.Filename.Contains('/') || file.Filename.Contains('\\'))
            throw new InvalidDataException("The selected file is not a safe mod JAR filename.");

        var destination = Path.Combine(modsDirectory, file.Filename);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? file.Size;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (total > 0) progress?.Report(Math.Clamp((double)received / total, 0, 1));
                }
            }

            if (!string.IsNullOrWhiteSpace(file.Sha512))
            {
                await using var stream = File.OpenRead(temporary);
                var actual = Convert.ToHexString(await SHA512.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                if (!actual.Equals(file.Sha512, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Modrinth SHA-512 verification failed. The downloaded file was rejected.");
            }
            File.Move(temporary, destination, true);
            progress?.Report(1);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public IReadOnlyList<string> GetInstalledMods(string instanceName)
    {
        var directory = Path.Combine(GetInstanceDirectory(instanceName), "mods");
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory)
                .Where(path => path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
    }

    public string SetModEnabled(string instanceName, string fileName, bool enabled)
    {
        var directory = Path.Combine(GetInstanceDirectory(instanceName), "mods");
        if (Path.GetFileName(fileName) != fileName || fileName.Contains('/') || fileName.Contains('\\'))
            throw new ArgumentException("Invalid mod filename.", nameof(fileName));
        var source = Path.Combine(directory, fileName);
        if (!File.Exists(source)) throw new FileNotFoundException("Mod file not found.", source);
        var targetName = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^9] : fileName;
        if (!enabled && !targetName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            targetName += ".disabled";
        var destination = Path.Combine(directory, targetName);
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            File.Move(source, destination, true);
        return destination;
    }

    public void RemoveMod(string instanceName, string fileName)
    {
        var directory = Path.GetFullPath(Path.Combine(GetInstanceDirectory(instanceName), "mods"));
        if (Path.GetFileName(fileName) != fileName || fileName.Contains('/') || fileName.Contains('\\'))
            throw new ArgumentException("Invalid mod filename.", nameof(fileName));
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mod path escaped the instance mods directory.");
        if (File.Exists(path)) File.Delete(path);
    }

    private string GetInstanceDirectory(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Invalid instance name.", nameof(name));
        return Path.Combine(_root, "instances", name);
    }
}
