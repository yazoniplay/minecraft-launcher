using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using NourLauncher.Core.Accounts;

namespace NourLauncher.Minecraft.Launching;

/// <summary>
/// A launch session: either a real Microsoft/Minecraft authentication result or an
/// explicitly requested offline identity (<see cref="IsOffline"/>).
/// </summary>
public sealed record MinecraftLaunchSession(
    string Username,
    string Uuid,
    string AccessToken,
    string UserType = "msa",
    string ClientId = "",
    string Xuid = "",
    bool IsOffline = false)
{
    /// <summary>
    /// Builds the offline session the vanilla client accepts in singleplayer/LAN:
    /// a deterministic name-derived UUID, no access token and the legacy user type.
    /// This is intentionally not an authenticated session — nothing here proves ownership.
    /// </summary>
    public static MinecraftLaunchSession CreateOffline(string username)
    {
        if (!OfflineAccount.IsValidUsername(username))
            throw new ArgumentException("Offline player names must be 3-16 letters, numbers or underscores.", nameof(username));

        var session = new MinecraftLaunchSession(
            Username: username,
            Uuid: OfflineAccount.ComputeUuid(username),
            AccessToken: "0",
            UserType: "legacy",
            IsOffline: true);
        session.Validate();
        return session;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Username))
            throw new InvalidOperationException("The account has no Minecraft profile name.");

        if (IsOffline)
        {
            // Offline identities are derived locally; they must stay within the vanilla name rules
            // so the UUID and the profile directory name remain valid.
            if (!OfflineAccount.IsValidUsername(Username))
                throw new InvalidOperationException("Offline player names must be 3-16 letters, numbers or underscores.");
            if (!Guid.TryParse(Uuid, out _))
                throw new InvalidOperationException("The offline identity UUID could not be derived.");
            return;
        }

        if (!Guid.TryParseExact(Uuid, "N", out _) && !Guid.TryParse(Uuid, out _))
            throw new InvalidOperationException("The authenticated account UUID is invalid.");
        if (string.IsNullOrWhiteSpace(AccessToken) ||
            AccessToken.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            AccessToken.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A valid Minecraft access token is required for online play.");
    }
}

public sealed record MinecraftLaunchRequest(
    string VersionId,
    string? InstanceName = null,
    int? Width = null,
    int? Height = null);

/// <summary>
/// Builds a launch command from Mojang version metadata and starts Minecraft.
/// Accepts a real authenticated session, or an offline session created with
/// <see cref="MinecraftLaunchSession.CreateOffline"/> when the user has enabled offline mode.
/// </summary>
public sealed class MinecraftLaunchService
{
    private readonly string _root;

    public MinecraftLaunchService(string? rootDirectory = null)
    {
        _root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyClient");
    }

    public Process Launch(
        MinecraftLaunchRequest request,
        MinecraftLaunchSession session,
        string javaExecutable)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        session.Validate();

        if (string.IsNullOrWhiteSpace(javaExecutable) || !File.Exists(javaExecutable))
            throw new FileNotFoundException("The managed Java executable was not found.", javaExecutable);

        ValidateVersionId(request.VersionId);
        var metadata = LoadResolvedMetadata(request.VersionId);
        var versionDirectory = Path.Combine(_root, "versions", request.VersionId);
        var clientJar = Path.Combine(versionDirectory, request.VersionId + ".jar");
        if (!File.Exists(clientJar))
            throw new FileNotFoundException($"Minecraft {request.VersionId} is not installed.", clientJar);

        var mainClass = GetString(metadata, "mainClass");
        if (string.IsNullOrWhiteSpace(mainClass))
            throw new InvalidDataException("The version metadata does not define a main class.");

        var gameDirectory = GetInstanceDirectory(request);
        Directory.CreateDirectory(gameDirectory);
        var nativesDirectory = Path.Combine(versionDirectory, "natives");
        Directory.CreateDirectory(nativesDirectory);

        var assetIndex = GetString(metadata, "assets") ?? request.VersionId;
        var assetRoot = Path.Combine(_root, "assets");
        var classpath = BuildClasspath(metadata, clientJar);
        var variables = BuildVariables(request, session, gameDirectory, assetRoot,
            assetIndex, nativesDirectory, classpath);

        var jvmArguments = ReadArgumentList(metadata, "jvm", "minecraftArguments", variables, legacy: false);
        var gameArguments = ReadArgumentList(metadata, "game", "minecraftArguments", variables, legacy: true);

        if (!jvmArguments.Any(a => a.Equals("-cp", StringComparison.OrdinalIgnoreCase) ||
                                   a.Equals("-classpath", StringComparison.OrdinalIgnoreCase)))
        {
            jvmArguments.Add("-cp");
            jvmArguments.Add(classpath);
        }

        if (!jvmArguments.Any(a => a.StartsWith("-Djava.library.path=", StringComparison.OrdinalIgnoreCase)))
            jvmArguments.Add("-Djava.library.path=" + nativesDirectory);

        var info = new ProcessStartInfo
        {
            FileName = javaExecutable,
            WorkingDirectory = gameDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        foreach (var argument in jvmArguments)
            info.ArgumentList.Add(argument);
        info.ArgumentList.Add(mainClass);
        foreach (var argument in gameArguments)
            info.ArgumentList.Add(argument);

        var process = Process.Start(info);
        return process ?? throw new InvalidOperationException("Windows could not start the Minecraft Java process.");
    }

    public IReadOnlyList<string> PreviewArguments(
        MinecraftLaunchRequest request,
        MinecraftLaunchSession session,
        string javaExecutable)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Validate();
        ValidateVersionId(request.VersionId);
        var metadata = LoadResolvedMetadata(request.VersionId);
        var clientJar = Path.Combine(_root, "versions", request.VersionId, request.VersionId + ".jar");
        if (!File.Exists(clientJar))
            throw new FileNotFoundException($"Minecraft {request.VersionId} is not installed.", clientJar);

        var gameDirectory = GetInstanceDirectory(request);
        var nativesDirectory = Path.Combine(_root, "versions", request.VersionId, "natives");
        var assetIndex = GetString(metadata, "assets") ?? request.VersionId;
        var classpath = BuildClasspath(metadata, clientJar);
        var variables = BuildVariables(request, session, gameDirectory,
            Path.Combine(_root, "assets"), assetIndex, nativesDirectory, classpath);
        var args = ReadArgumentList(metadata, "jvm", "minecraftArguments", variables, legacy: false);
        if (!args.Any(a => a is "-cp" or "-classpath"))
        {
            args.Add("-cp");
            args.Add(classpath);
        }
        if (!args.Any(a => a.StartsWith("-Djava.library.path=", StringComparison.OrdinalIgnoreCase)))
            args.Add("-Djava.library.path=" + nativesDirectory);
        args.Add(GetString(metadata, "mainClass") ?? throw new InvalidDataException("Missing mainClass."));
        args.AddRange(ReadArgumentList(metadata, "game", "minecraftArguments", variables, legacy: true));
        return new[] { javaExecutable }.Concat(args).ToArray();
    }

    private JsonElement LoadResolvedMetadata(string versionId, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!visited.Add(versionId))
            throw new InvalidDataException("Minecraft version inheritance contains a cycle.");

        var path = Path.Combine(_root, "versions", versionId, versionId + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Version metadata for {versionId} is missing. Install this version first.", path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var current = document.RootElement.Clone();
        if (!current.TryGetProperty("inheritsFrom", out var inheritsElement))
            return current;

        var parentId = inheritsElement.GetString();
        if (string.IsNullOrWhiteSpace(parentId))
            return current;
        ValidateVersionId(parentId);
        var parent = LoadResolvedMetadata(parentId, visited);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in parent.EnumerateObject())
            {
                if (property.NameEquals("libraries") && current.TryGetProperty("libraries", out var childLibraries))
                {
                    writer.WritePropertyName("libraries");
                    writer.WriteStartArray();
                    foreach (var item in property.Value.EnumerateArray()) item.WriteTo(writer);
                    foreach (var item in childLibraries.EnumerateArray()) item.WriteTo(writer);
                    writer.WriteEndArray();
                    names.Add("libraries");
                }
                else if (current.TryGetProperty(property.Name, out var childProperty))
                {
                    childProperty.WriteTo(writer);
                    names.Add(property.Name);
                }
                else
                {
                    property.WriteTo(writer);
                    names.Add(property.Name);
                }
            }

            foreach (var property in current.EnumerateObject())
            {
                if (property.NameEquals("inheritsFrom") || names.Contains(property.Name)) continue;
                property.WriteTo(writer);
            }
            if (!names.Contains("libraries") && current.TryGetProperty("libraries", out var onlyChildLibraries))
            {
                writer.WritePropertyName("libraries");
                onlyChildLibraries.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var merged = JsonDocument.Parse(stream.ToArray());
        return merged.RootElement.Clone();
    }

    private string BuildClasspath(JsonElement metadata, string clientJar)
    {
        var entries = new List<string>();
        if (metadata.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateArray())
            {
                if (!IsAllowed(library)) continue;
                if (!library.TryGetProperty("downloads", out var downloads) ||
                    !downloads.TryGetProperty("artifact", out var artifact) ||
                    !artifact.TryGetProperty("path", out var pathElement)) continue;
                var relative = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(relative)) continue;
                var libraryPath = SafeCombine(Path.Combine(_root, "libraries"), relative);
                if (File.Exists(libraryPath)) entries.Add(libraryPath);
            }
        }
        entries.Add(clientJar);
        return string.Join(Path.PathSeparator, entries.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private Dictionary<string, string> BuildVariables(
        MinecraftLaunchRequest request,
        MinecraftLaunchSession session,
        string gameDirectory,
        string assetRoot,
        string assetIndex,
        string nativesDirectory,
        string classpath)
    {
        var uuid = Guid.Parse(session.Uuid).ToString("N");
        // Legacy (pre-1.6) argument sets reference ${auth_session} and ${game_assets}/
        // ${user_properties}; modern versions ignore them, but supplying them keeps
        // old alpha/beta versions launchable instead of failing on unresolved tokens.
        var legacyAssetRoot = Path.Combine(assetRoot, "virtual", "legacy");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = session.Username,
            ["version_name"] = request.VersionId,
            ["game_directory"] = gameDirectory,
            ["assets_root"] = assetRoot,
            ["game_assets"] = legacyAssetRoot,
            ["assets_index_name"] = assetIndex,
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = session.AccessToken,
            ["auth_session"] = $"token:{session.AccessToken}:{uuid}",
            ["user_properties"] = "{}",
            ["user_type"] = session.UserType,
            ["version_type"] = "release",
            ["natives_directory"] = nativesDirectory,
            ["launcher_name"] = "Sky Client",
            ["launcher_version"] = "0.2",
            ["classpath"] = classpath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = Path.Combine(_root, "libraries"),
            ["clientid"] = session.ClientId,
            ["auth_xuid"] = session.Xuid,
            ["resolution_width"] = (request.Width ?? 854).ToString(),
            ["resolution_height"] = (request.Height ?? 480).ToString()
        };
    }

    private List<string> ReadArgumentList(
        JsonElement metadata,
        string argumentKind,
        string legacyProperty,
        IReadOnlyDictionary<string, string> variables,
        bool legacy)
    {
        var result = new List<string>();
        if (metadata.TryGetProperty("arguments", out var arguments) &&
            arguments.TryGetProperty(argumentKind, out var values) &&
            values.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in values.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    result.Add(ReplaceVariables(item.GetString()!, variables));
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object || !IsAllowed(item)) continue;
                if (!item.TryGetProperty("value", out var value)) continue;
                if (value.ValueKind == JsonValueKind.String)
                    result.Add(ReplaceVariables(value.GetString()!, variables));
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var part in value.EnumerateArray())
                        if (part.ValueKind == JsonValueKind.String)
                            result.Add(ReplaceVariables(part.GetString()!, variables));
            }
            return result;
        }

        if (legacy && metadata.TryGetProperty(legacyProperty, out var legacyArgs) &&
            legacyArgs.ValueKind == JsonValueKind.String)
        {
            foreach (var token in TokenizeLegacyArguments(legacyArgs.GetString()!))
                result.Add(ReplaceVariables(token, variables));
        }
        return result;
    }

    private static IEnumerable<string> TokenizeLegacyArguments(string value)
    {
        foreach (Match match in Regex.Matches(value, "\"([^\"]*)\"|'([^']*)'|(\\S+)"))
        {
            if (match.Groups[1].Success) yield return match.Groups[1].Value;
            else if (match.Groups[2].Success) yield return match.Groups[2].Value;
            else yield return match.Groups[3].Value;
        }
    }

    private static string ReplaceVariables(string value, IReadOnlyDictionary<string, string> variables)
    {
        return Regex.Replace(value, "\\$\\{([^}]+)\\}", match =>
            variables.TryGetValue(match.Groups[1].Value, out var replacement) ? replacement : match.Value);
    }

    private static bool IsAllowed(JsonElement item)
    {
        if (!item.TryGetProperty("rules", out var rules)) return true;
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var matches = true;
            if (rule.TryGetProperty("features", out var features))
            {
                foreach (var feature in features.EnumerateObject())
                {
                    if (feature.Name == "has_custom_resolution" && feature.Value.ValueKind == JsonValueKind.True) continue;
                    matches = false;
                    break;
                }
            }
            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("name", out var name) && name.GetString() != "windows") matches = false;
                if (os.TryGetProperty("arch", out var arch) && arch.GetString() is string value)
                {
                    if (value is "x86_64" or "amd64" && !Environment.Is64BitOperatingSystem) matches = false;
                    if (value == "x86" && Environment.Is64BitOperatingSystem) matches = false;
                }
            }
            if (matches) allowed = rule.TryGetProperty("action", out var action) && action.GetString() == "allow";
        }
        return allowed;
    }

    private string GetInstanceDirectory(MinecraftLaunchRequest request)
    {
        var name = string.IsNullOrWhiteSpace(request.InstanceName) ? request.VersionId : request.InstanceName.Trim();
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Invalid instance name.", nameof(request));
        return Path.Combine(_root, "instances", name);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A library path escaped the managed library directory.");
        return fullPath;
    }

    private static void ValidateVersionId(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId) || versionId is "." or ".." ||
            versionId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-' and not '.'))
            throw new ArgumentException("Invalid Minecraft version ID.", nameof(versionId));
    }
}
