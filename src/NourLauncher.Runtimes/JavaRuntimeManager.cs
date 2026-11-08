using System.IO.Compression;
using System.Net.Http;

namespace NourLauncher.Runtimes;

/// <summary>
/// Installs a private Eclipse Temurin Java 25 JRE for Nour Launcher.
/// This runtime is optional; Minecraft versions must use a compatible Java major version.
/// </summary>
public sealed class JavaRuntimeManager
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly string _runtimeRoot;

    public JavaRuntimeManager(string? rootDirectory = null)
    {
        var appData = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NourLauncher");
        _runtimeRoot = Path.Combine(appData, "runtimes", "temurin-25");
    }

    public string RuntimeDirectory => _runtimeRoot;
    public string JavaExecutablePath => Path.Combine(_runtimeRoot, "bin", "javaw.exe");
    public bool IsJava25Installed => File.Exists(JavaExecutablePath);

    public async Task InstallJava25Async(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsJava25Installed) return;

        Directory.CreateDirectory(Path.GetDirectoryName(_runtimeRoot)!);
        var archivePath = Path.Combine(Path.GetTempPath(), $"nour-temurin25-{Guid.NewGuid():N}.zip");
        var stagingPath = _runtimeRoot + ".installing-" + Guid.NewGuid().ToString("N");

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.adoptium.net/v3/binary/latest/25/ga/windows/x64/jre/hotspot/normal/eclipse");
            request.Headers.UserAgent.ParseAdd("NourLauncher/0.1");

            using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1024 * 128];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (total is > 0) progress?.Report((double)received / total.Value * 0.85);
                }
            }

            Directory.CreateDirectory(stagingPath);
            ZipFile.ExtractToDirectory(archivePath, stagingPath);

            var javaw = Directory.GetFiles(stagingPath, "javaw.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Replace('/', '\\').Contains("\\bin\\", StringComparison.OrdinalIgnoreCase));
            if (javaw is null)
                throw new InvalidDataException("The downloaded Java archive did not contain bin\\javaw.exe.");

            var extractedRoot = Directory.GetParent(Path.GetDirectoryName(javaw)!)!.FullName;
            if (Directory.Exists(_runtimeRoot)) Directory.Delete(_runtimeRoot, recursive: true);
            Directory.Move(extractedRoot, _runtimeRoot);

            if (!IsJava25Installed)
                throw new InvalidDataException("Java 25 installation completed without a usable javaw.exe.");

            progress?.Report(1);
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
        }
    }
}
