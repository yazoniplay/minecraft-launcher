using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace NourLauncher.Minecraft.Installation;

public sealed record InstallationProgress(string Message, int CompletedFiles, int TotalFiles, double? OverallPercentage = null)
{
    public double Percentage => OverallPercentage ?? (TotalFiles == 0 ? 0 : (double)CompletedFiles / TotalFiles);
}

public sealed class MinecraftInstallationService
{
    private static readonly Uri ManifestUri = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    private readonly HttpClient _http;
    private readonly string _root;

    public MinecraftInstallationService(HttpClient? httpClient = null, string? rootDirectory = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyClient");
    }

    public string RootDirectory => _root;

    public async Task<IReadOnlyList<InstalledVersionInfo>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(ManifestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var versions = new List<InstalledVersionInfo>();
        foreach (var item in document.RootElement.GetProperty("versions").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var type = item.GetProperty("type").GetString() ?? "unknown";
            var releaseTime = item.GetProperty("releaseTime").GetDateTimeOffset();
            var metadataUrl = item.GetProperty("url").GetString() ?? "";
            var sha1 = item.TryGetProperty("sha1", out var hash) ? hash.GetString() : null;
            versions.Add(new InstalledVersionInfo(id, type, releaseTime, metadataUrl, sha1,
                File.Exists(GetVersionJsonPath(id)) && File.Exists(GetClientJarPath(id))));
        }
        return versions;
    }

    public async Task InstallAsync(
        string versionId,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionId) || versionId is "." or ".." ||
            versionId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-' and not '.'))
            throw new ArgumentException("Invalid Minecraft version ID.", nameof(versionId));

        var manifest = await GetVersionEntryAsync(versionId, cancellationToken);
        var versionJsonPath = GetVersionJsonPath(versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(versionJsonPath)!);
        await DownloadVerifiedAsync(new Uri(manifest.Url), versionJsonPath, manifest.Sha1, cancellationToken);

        using var versionDocument = JsonDocument.Parse(await File.ReadAllTextAsync(versionJsonPath, cancellationToken));
        var root = versionDocument.RootElement;
        if (!root.TryGetProperty("downloads", out var downloads) ||
            !downloads.TryGetProperty("client", out var client))
            throw new InvalidOperationException($"Minecraft {versionId} does not provide a downloadable client jar.");

        var tasks = new List<DownloadItem>();
        var nativeArchives = new List<string>();
        tasks.Add(new DownloadItem(client.GetProperty("url").GetString()!,
            GetClientJarPath(versionId), GetOptionalString(client, "sha1")));

        if (root.TryGetProperty("assetIndex", out var assetIndex))
        {
            var indexId = assetIndex.GetProperty("id").GetString()!;
            var indexPath = Path.Combine(_root, "assets", "indexes", indexId + ".json");
            tasks.Add(new DownloadItem(assetIndex.GetProperty("url").GetString()!, indexPath,
                GetOptionalString(assetIndex, "sha1")));
        }

        if (root.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateArray())
            {
                if (!IsAllowed(library)) continue;
                if (!library.TryGetProperty("downloads", out var libraryDownloads)) continue;
                if (libraryDownloads.TryGetProperty("artifact", out var artifact))
                {
                    var path = GetOptionalString(artifact, "path");
                    if (!string.IsNullOrWhiteSpace(path))
                        tasks.Add(new DownloadItem(artifact.GetProperty("url").GetString()!,
                            SafeCombine(Path.Combine(_root, "libraries"), path),
                            GetOptionalString(artifact, "sha1")));
                }

                if (libraryDownloads.TryGetProperty("classifiers", out var classifiers) &&
                    library.TryGetProperty("natives", out var natives) &&
                    natives.TryGetProperty("windows", out var windowsNative))
                {
                    var classifierName = windowsNative.GetString()?.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                    if (!string.IsNullOrWhiteSpace(classifierName) &&
                        classifiers.TryGetProperty(classifierName, out var nativeArtifact))
                    {
                        var nativePath = GetOptionalString(nativeArtifact, "path");
                        if (!string.IsNullOrWhiteSpace(nativePath))
                        {
                            var nativeArchive = SafeCombine(Path.Combine(_root, "libraries"), nativePath);
                            nativeArchives.Add(nativeArchive);
                            tasks.Add(new DownloadItem(nativeArtifact.GetProperty("url").GetString()!,
                                nativeArchive, GetOptionalString(nativeArtifact, "sha1")));
                        }
                    }
                }
            }
        }

        var completed = 0;
        var downloadTasks = tasks.DistinctBy(x => x.Destination).ToArray();
        var total = downloadTasks.Length;
        await Parallel.ForEachAsync(downloadTasks,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                await DownloadVerifiedAsync(new Uri(item.Url), item.Destination, item.Sha1, token);
                var count = Interlocked.Increment(ref completed);
                progress?.Report(new InstallationProgress($"Downloaded {Path.GetFileName(item.Destination)}", count, total, total == 0 ? 0 : 0.5 * count / total));
            });

        if (root.TryGetProperty("assetIndex", out var indexMetadata))
        {
            var indexId = indexMetadata.GetProperty("id").GetString()!;
            var indexPath = Path.Combine(_root, "assets", "indexes", indexId + ".json");
            using var indexDocument = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken));
            if (indexDocument.RootElement.TryGetProperty("objects", out var objects))
            {
                var objectTasks = objects.EnumerateObject().Select(property =>
                {
                    var hash = property.Value.GetProperty("hash").GetString()!;
                    var destination = Path.Combine(_root, "assets", "objects", hash[..2], hash);
                    return new DownloadItem($"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                        destination, hash);
                }).ToArray();

                var assetCompleted = 0;
                var assetTotal = objectTasks.Length;
                await Parallel.ForEachAsync(objectTasks,
                    new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = cancellationToken },
                    async (item, token) =>
                    {
                        await DownloadVerifiedAsync(new Uri(item.Url), item.Destination, item.Sha1, token);
                        var count = Interlocked.Increment(ref assetCompleted);
                        progress?.Report(new InstallationProgress($"Downloading assets ({count}/{assetTotal})",
                            total + count, total + assetTotal, assetTotal == 0 ? 1 : 0.5 + 0.5 * count / assetTotal));
                    });
                total += assetTotal;
            }
        }

        var nativesDirectory = Path.Combine(_root, "versions", versionId, "natives");
        foreach (var archive in nativeArchives.Distinct(StringComparer.OrdinalIgnoreCase))
            ExtractNativeArchive(archive, nativesDirectory);

        var marker = Path.Combine(_root, "versions", versionId, "installed.json");
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new
        {
            version = versionId,
            installedAt = DateTimeOffset.UtcNow,
            clientJar = GetClientJarPath(versionId)
        }), cancellationToken);
        progress?.Report(new InstallationProgress($"Minecraft {versionId} is installed.", total, total));
    }

    public async Task<int> GetJavaMajorVersionAsync(string versionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionId) || versionId is "." or ".." ||
            versionId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-' and not '.'))
            throw new ArgumentException("Invalid Minecraft version ID.", nameof(versionId));

        var metadataPath = GetVersionJsonPath(versionId);
        if (!File.Exists(metadataPath))
        {
            var manifest = await GetVersionEntryAsync(versionId, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
            await DownloadVerifiedAsync(new Uri(manifest.Url), metadataPath, manifest.Sha1, cancellationToken);
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath, cancellationToken));
        if (document.RootElement.TryGetProperty("inheritsFrom", out var inherited) &&
            inherited.GetString() is string parentVersion && !string.IsNullOrWhiteSpace(parentVersion))
            return await GetJavaMajorVersionAsync(parentVersion, cancellationToken);

        if (document.RootElement.TryGetProperty("javaVersion", out var javaVersion) &&
            javaVersion.TryGetProperty("majorVersion", out var major) && major.TryGetInt32(out var requiredMajor))
            return requiredMajor;

        // Mojang added javaVersion metadata in the modern version format.
        // Legacy versions before 1.17 use Java 8; 1.17 uses Java 16; 1.18+ defaults to Java 17.
        var parts = versionId.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[1], out var minor))
        {
            if (minor <= 16) return 8;
            if (minor == 17) return 16;
            return 17;
        }
        return 8;
    }

    public bool IsInstalled(string versionId) =>
        File.Exists(GetVersionJsonPath(versionId)) &&
        File.Exists(GetClientJarPath(versionId)) &&
        File.Exists(Path.Combine(_root, "versions", versionId, "installed.json"));

    private async Task<ManifestEntry> GetVersionEntryAsync(string id, CancellationToken token)
    {
        using var response = await _http.GetAsync(ManifestUri, token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        foreach (var item in document.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (item.GetProperty("id").GetString() == id)
                return new ManifestEntry(item.GetProperty("url").GetString()!,
                    GetOptionalString(item, "sha1"));
        }
        throw new InvalidOperationException($"Minecraft version '{id}' was not found in Mojang's version manifest.");
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
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<bool> MatchesSha1Async(string path, string? expected, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, token)).ToLowerInvariant();
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractNativeArchive(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                continue;
            var output = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A native archive entry escaped the natives directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            entry.ExtractToFile(output, true);
        }
    }

    private static bool IsAllowed(JsonElement library)
    {
        if (!library.TryGetProperty("rules", out var rules)) return true;
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var matches = true;
            if (rule.TryGetProperty("features", out _)) matches = false;
            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("name", out var name) && name.GetString() != "windows") matches = false;
                if (os.TryGetProperty("arch", out var arch) && arch.GetString() is string value)
                {
                    var is64Bit = Environment.Is64BitOperatingSystem;
                    if (value == "x86" && is64Bit) matches = false;
                    if (value is "x86_64" or "amd64" && !is64Bit) matches = false;
                }
            }
            if (matches) allowed = rule.GetProperty("action").GetString() == "allow";
        }
        return allowed;
    }

    private string GetVersionJsonPath(string id) => Path.Combine(_root, "versions", id, id + ".json");
    private string GetClientJarPath(string id) => Path.Combine(_root, "versions", id, id + ".jar");

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A library path escaped the managed library directory.");
        return fullPath;
    }

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private sealed record ManifestEntry(string Url, string? Sha1);
    private sealed record DownloadItem(string Url, string Destination, string? Sha1);
}

public sealed record InstalledVersionInfo(
    string Id, string Type, DateTimeOffset ReleaseTime, string MetadataUrl, string? Sha1, bool IsInstalled);
