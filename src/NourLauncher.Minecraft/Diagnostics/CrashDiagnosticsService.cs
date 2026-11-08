using System.Text;
using System.Text.RegularExpressions;

namespace NourLauncher.Minecraft.Diagnostics;

public sealed record CrashReport(
    string FilePath, string FileName, DateTimeOffset LastModified,
    string Summary, string? LikelyCause, string Excerpt);

/// <summary>Finds Minecraft logs and crash reports and extracts useful, local-only diagnostics.</summary>
public sealed class CrashDiagnosticsService
{
    private readonly string _root;

    public CrashDiagnosticsService(string? rootDirectory = null)
    {
        _root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyClient");
    }

    public IReadOnlyList<CrashReport> GetRecentReports(string? instanceName = null, int limit = 30)
    {
        var folders = new List<string>();
        if (!string.IsNullOrWhiteSpace(instanceName))
        {
            folders.Add(Path.Combine(_root, "instances", instanceName, "crash-reports"));
            folders.Add(Path.Combine(_root, "instances", instanceName, "logs"));
        }
        else
        {
            var instances = Path.Combine(_root, "instances");
            if (Directory.Exists(instances))
                foreach (var instance in Directory.EnumerateDirectories(instances))
                {
                    folders.Add(Path.Combine(instance, "crash-reports"));
                    folders.Add(Path.Combine(instance, "logs"));
                }
            folders.Add(Path.Combine(_root, "crash-reports"));
            folders.Add(Path.Combine(_root, "logs"));
        }

        return folders.Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder)
                .Where(path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(file => Analyze(file.FullName))
            .ToArray();
    }

    public CrashReport Analyze(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Crash log not found.", path);
        var text = ReadTail(path, 220_000);
        var cause = DetectCause(text);
        var excerpt = string.Join(Environment.NewLine, text.Split('\n')
            .Where(line => Regex.IsMatch(line, "ERROR|FATAL|Exception|Caused by:", RegexOptions.IgnoreCase))
            .TakeLast(18)).Trim();
        if (excerpt.Length == 0) excerpt = string.Join(Environment.NewLine, text.Split('\n').TakeLast(12));
        return new CrashReport(path, info.Name, info.LastWriteTime, cause?.Summary ?? "No known crash signature detected.",
            cause?.Cause, excerpt);
    }

    public string BuildDiagnosticReport(string path)
    {
        var report = Analyze(path);
        var builder = new StringBuilder();
        builder.AppendLine("SKY CLIENT CRASH DIAGNOSTIC");
        builder.AppendLine("Generated: " + DateTimeOffset.Now.ToString("O"));
        builder.AppendLine("File: " + report.FileName);
        builder.AppendLine("Modified: " + report.LastModified.ToString("O"));
        builder.AppendLine("Summary: " + report.Summary);
        if (!string.IsNullOrWhiteSpace(report.LikelyCause))
            builder.AppendLine("Likely cause: " + report.LikelyCause);
        builder.AppendLine();
        builder.AppendLine("Relevant log lines:");
        builder.AppendLine(report.Excerpt);
        return builder.ToString();
    }

    private static string ReadTail(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var count = (int)Math.Min(stream.Length, maxBytes);
        stream.Seek(-count, SeekOrigin.End);
        var buffer = new byte[count];
        _ = stream.Read(buffer, 0, count);
        return Encoding.UTF8.GetString(buffer);
    }

    private static (string Summary, string Cause)? DetectCause(string text)
    {
        var patterns = new (string Pattern, string Summary, string Cause)[]
        {
            ("OutOfMemoryError|Could not reserve enough space", "Java ran out of memory.", "The game or modpack needs more memory, or another process is using too much RAM. Check the allocated memory and close unnecessary applications."),
            ("UnsupportedClassVersionError", "A mod or library needs a newer Java version.", "Use the Java major version required by the Minecraft version and check whether the mod supports this game version."),
            ("NoClassDefFoundError|ClassNotFoundException", "A required class or dependency is missing.", "Check for a missing dependency, incompatible mod version, or incomplete installation."),
            ("MixinApplyError|MixinTransformerError|InjectionError", "A mod mixin failed during startup.", "One of the installed mods is incompatible with this Minecraft version or another mod. Test by disabling recently added mods."),
            ("ModResolutionException|Incompatible mod set", "The mod loader found incompatible mods.", "Check each mod's Minecraft version and loader compatibility, and install required dependencies."),
            ("GLFW error|OpenGL|Pixel format not accelerated", "Graphics initialization failed.", "Update the graphics driver and check whether the GPU supports the Minecraft version's graphics requirements."),
            ("Failed to download|Connection timed out|UnknownHostException", "A network request failed.", "Check the internet connection, firewall, and whether the download host is reachable."),
            ("Invalid or corrupt jarfile|ZipException", "A game or mod JAR appears incomplete or corrupt.", "Reinstall the affected version or mod and verify the downloaded file.")
        };
        foreach (var item in patterns)
            if (Regex.IsMatch(text, item.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return (item.Summary, item.Cause);
        return null;
    }
}
