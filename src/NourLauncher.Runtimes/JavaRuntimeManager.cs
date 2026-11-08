using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;

namespace NourLauncher.Runtimes;

/// <summary>Downloads and validates private Eclipse Temurin runtimes, isolated by Java major version.</summary>
public sealed class JavaRuntimeManager
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly string _runtimeRoot;

    public JavaRuntimeManager(string? rootDirectory = null)
    {
        var appData = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyClient");
        _runtimeRoot = Path.Combine(appData, "runtimes");
    }

    public string GetRuntimeDirectory(int majorVersion) =>
        Path.Combine(_runtimeRoot, $"temurin-{majorVersion}");

    public string GetJavaExecutablePath(int majorVersion) =>
        Path.Combine(GetRuntimeDirectory(majorVersion), "bin", "javaw.exe");

    public bool IsInstalled(int majorVersion) =>
        File.Exists(GetJavaExecutablePath(majorVersion));

    public IReadOnlyList<int> GetInstalledMajors() =>
        Directory.Exists(_runtimeRoot)
            ? Directory.GetDirectories(_runtimeRoot, "temurin-*")
                .Select(Path.GetFileName)
                .Select(name => name is not null && int.TryParse(name["temurin-".Length..], out var major) ? major : 0)
                .Where(major => major > 0 && IsInstalled(major))
                .Order()
                .ToArray()
            : [];

    public async Task<string> EnsureRuntimeAsync(
        int majorVersion,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (majorVersion < 8 || majorVersion > 99)
            throw new ArgumentOutOfRangeException(nameof(majorVersion), "Unsupported Java major version.");

        var executable = GetJavaExecutablePath(majorVersion);
        if (await IsUsableAsync(executable, majorVersion, cancellationToken))
            return executable;

        Directory.CreateDirectory(_runtimeRoot);
        var archivePath = Path.Combine(Path.GetTempPath(), $"skyclient-temurin-{majorVersion}-{Guid.NewGuid():N}.zip");
        var stagingPath = Path.Combine(_runtimeRoot, $".staging-{majorVersion}-{Guid.NewGuid():N}");
        var replacementPath = GetRuntimeDirectory(majorVersion) + ".new-" + Guid.NewGuid().ToString("N");
        var backupPath = GetRuntimeDirectory(majorVersion) + ".backup-" + Guid.NewGuid().ToString("N");

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.adoptium.net/v3/binary/latest/{majorVersion}/ga/windows/x64/jre/hotspot/normal/eclipse");
            request.Headers.UserAgent.ParseAdd("SkyClient/0.2");

            using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (total is > 0) progress?.Report((double)received / total.Value * 0.75);
                }
            }

            Directory.CreateDirectory(stagingPath);
            ZipFile.ExtractToDirectory(archivePath, stagingPath);
            var javaw = Directory.GetFiles(stagingPath, "javaw.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Replace('/', '\\').Contains("\\bin\\", StringComparison.OrdinalIgnoreCase));
            if (javaw is null)
                throw new InvalidDataException($"The Java {majorVersion} archive did not contain bin\\javaw.exe.");

            var extractedRoot = Directory.GetParent(Path.GetDirectoryName(javaw)!)!.FullName;
            Directory.Move(extractedRoot, replacementPath);
            var candidate = Path.Combine(replacementPath, "bin", "java.exe");
            if (!await IsUsableAsync(candidate, majorVersion, cancellationToken))
                throw new InvalidDataException($"The downloaded runtime did not report Java {majorVersion}.");

            var finalPath = GetRuntimeDirectory(majorVersion);
            if (Directory.Exists(finalPath)) Directory.Move(finalPath, backupPath);
            try
            {
                Directory.Move(replacementPath, finalPath);
            }
            catch
            {
                if (Directory.Exists(backupPath) && !Directory.Exists(finalPath))
                    Directory.Move(backupPath, finalPath);
                throw;
            }

            if (Directory.Exists(backupPath)) Directory.Delete(backupPath, true);
            progress?.Report(1);
            return GetJavaExecutablePath(majorVersion);
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, true);
            if (Directory.Exists(replacementPath)) Directory.Delete(replacementPath, true);
            if (Directory.Exists(backupPath) && !Directory.Exists(GetRuntimeDirectory(majorVersion)))
                Directory.Move(backupPath, GetRuntimeDirectory(majorVersion));
        }
    }

    private static async Task<bool> IsUsableAsync(string executable, int expectedMajor, CancellationToken token)
    {
        if (!File.Exists(executable)) return false;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Path.GetDirectoryName(executable)!, "java.exe"),
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            if (!process.Start()) return false;
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            var output = (await stdout) + (await stderr);
            if (process.ExitCode != 0) return false;
            var marker = "version \"";
            var index = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var versionParts = output[(index + marker.Length)..].Split('"', '+', '-')[0].Split('.');
            if (!int.TryParse(versionParts[0], out var actualMajor)) return false;
            if (actualMajor == 1 && versionParts.Length > 1 && int.TryParse(versionParts[1], out var legacyMajor))
                actualMajor = legacyMajor;
            return actualMajor == expectedMajor;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch { return false; }
    }
}
