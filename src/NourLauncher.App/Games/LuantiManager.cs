using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NourLauncher.App.Games;

public sealed class LuantiManager
{
    private static readonly HttpClient Http = CreateClient();
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyClient", "luanti");
    private string InstallRoot => Path.Combine(_root, "engine");
    private string UserRoot => Path.Combine(_root, "user");
    private string GamesRoot => Path.Combine(UserRoot, "games");
    private string ConfigPath => Path.Combine(UserRoot, "minetest.conf");
    public string ExecutablePath => Path.Combine(InstallRoot, "bin", "luanti.exe");
    public bool IsInstalled => File.Exists(ExecutablePath) &&
        File.Exists(Path.Combine(GamesRoot, "minetest_game", "game.conf"));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SkyClient/1.0");
        return client;
    }

    public async Task InstallAsync(string username, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report("Checking the latest official Luanti release…");
            using var releaseResponse = await Http.GetAsync(
                "https://api.github.com/repos/luanti-org/luanti/releases/latest",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            releaseResponse.EnsureSuccessStatusCode();
            await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var release = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken);
            var assets = release.RootElement.GetProperty("assets").EnumerateArray();
            var asset = assets.FirstOrDefault(a =>
                a.GetProperty("name").GetString()?.EndsWith("-win64.zip", StringComparison.OrdinalIgnoreCase) == true);
            if (asset.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("The latest Luanti release has no Windows x64 portable archive.");
            var downloadUrl = asset.GetProperty("browser_download_url").GetString()
                ?? throw new InvalidOperationException("Luanti release did not provide a download URL.");
            var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
            var archivePath = Path.Combine(staging, "luanti.zip");

            progress?.Report("Downloading Luanti for Windows x64…");
            await DownloadAsync(downloadUrl, archivePath, progress, cancellationToken);
            if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("Verifying Luanti download…");
                await using var file = File.OpenRead(archivePath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
                if (!hash.Equals(digest["sha256:".Length..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Luanti archive SHA-256 verification failed.");
            }

            progress?.Report("Extracting Luanti…");
            var engineExtract = Path.Combine(staging, "engine-extract");
            ZipFile.ExtractToDirectory(archivePath, engineExtract);
            var executable = Directory.GetFiles(engineExtract, "luanti.exe", SearchOption.AllDirectories)
                .FirstOrDefault(p => Path.GetDirectoryName(p)?.EndsWith("bin", StringComparison.OrdinalIgnoreCase) == true)
                ?? throw new InvalidDataException("The Luanti archive did not contain bin\\luanti.exe.");
            var engineRoot = Directory.GetParent(Path.GetDirectoryName(executable)!)!.FullName;

            progress?.Report("Downloading the official Minetest Game package…");
            var gameArchive = Path.Combine(staging, "minetest-game.zip");
            await DownloadAsync(
                "https://codeload.github.com/luanti-org/minetest_game/zip/refs/heads/master",
                gameArchive, progress, cancellationToken);

            progress?.Report("Installing the game files…");
            var gameExtract = Path.Combine(staging, "game-extract");
            ZipFile.ExtractToDirectory(gameArchive, gameExtract);
            var gameConfig = Directory.GetFiles(gameExtract, "game.conf", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (gameConfig is null)
                throw new InvalidDataException("The Minetest Game archive did not contain game.conf.");
            var gameRoot = Path.GetDirectoryName(gameConfig)!;

            var engineBackup = InstallRoot + ".old";
            if (Directory.Exists(engineBackup)) Directory.Delete(engineBackup, true);
            if (Directory.Exists(InstallRoot)) Directory.Move(InstallRoot, engineBackup);
            try
            {
                if (Directory.Exists(InstallRoot)) Directory.Delete(InstallRoot, true);
                Directory.Move(engineRoot, InstallRoot);
                Directory.CreateDirectory(GamesRoot);
                var installedGame = Path.Combine(GamesRoot, "minetest_game");
                if (Directory.Exists(installedGame)) Directory.Delete(installedGame, true);
                Directory.Move(gameRoot, installedGame);
                if (Directory.Exists(engineBackup)) Directory.Delete(engineBackup, true);
            }
            catch
            {
                if (Directory.Exists(InstallRoot)) Directory.Delete(InstallRoot, true);
                if (Directory.Exists(engineBackup)) Directory.Move(engineBackup, InstallRoot);
                throw;
            }

            await WriteConfigAsync(username, cancellationToken);
            if (!File.Exists(ExecutablePath))
                throw new InvalidDataException("Luanti installation completed but luanti.exe was not found.");
            progress?.Report("Luanti is installed and ready to play.");
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public async Task LaunchAsync(string username, CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
            throw new InvalidOperationException("Install Luanti and Minetest Game before launching.");
        await WriteConfigAsync(username, cancellationToken);
        var start = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(ConfigPath);
        start.Environment["MINETEST_USER_PATH"] = UserRoot;
        Process.Start(start) ?? throw new InvalidOperationException("Windows could not start Luanti.");
    }

    private async Task WriteConfigAsync(string username, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(UserRoot);
        var safeName = new string((username ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(16).ToArray());
        if (safeName.Length < 3) safeName = "Player";
        var config = $"name = {safeName}\ncreative_mode = true\nenable_damage = false\n";
        await File.WriteAllTextAsync(ConfigPath, config, new UTF8Encoding(false), cancellationToken);
    }

    private static async Task DownloadAsync(string url, string destination, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (total is > 0)
                progress?.Report($"Downloading… {received * 100d / total.Value:0}%");
        }
    }
}
