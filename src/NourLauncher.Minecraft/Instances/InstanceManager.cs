using System.Text.Json;

namespace NourLauncher.Minecraft.Instances;

public sealed record MinecraftInstance(
    string Name, string MinecraftVersion, string Loader, string Directory,
    DateTimeOffset CreatedAt, string? Notes = null);

/// <summary>Creates and manages isolated game directories for Minecraft instances.</summary>
public sealed class InstanceManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;

    public InstanceManager(string? rootDirectory = null)
    {
        _root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyClient");
    }

    public IReadOnlyList<MinecraftInstance> GetInstances()
    {
        var root = Path.Combine(_root, "instances");
        if (!Directory.Exists(root)) return Array.Empty<MinecraftInstance>();
        var result = new List<MinecraftInstance>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var metadataPath = Path.Combine(directory, "instance.json");
            if (!File.Exists(metadataPath)) continue;
            try
            {
                var instance = JsonSerializer.Deserialize<MinecraftInstance>(
                    File.ReadAllText(metadataPath), JsonOptions);
                if (instance is not null) result.Add(instance);
            }
            catch (JsonException) { }
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<MinecraftInstance> CreateAsync(
        string name, string minecraftVersion, string loader = "vanilla",
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(minecraftVersion) ||
            minecraftVersion.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-' and not '.'))
            throw new ArgumentException("Invalid Minecraft version.", nameof(minecraftVersion));

        var root = Path.Combine(_root, "instances");
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, name);
        if (Directory.Exists(directory))
            throw new InvalidOperationException($"An instance named '{name}' already exists.");

        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "mods"));
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        Directory.CreateDirectory(Path.Combine(directory, "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(directory, "shaderpacks"));
        Directory.CreateDirectory(Path.Combine(directory, "screenshots"));

        var instance = new MinecraftInstance(name, minecraftVersion, loader, directory, DateTimeOffset.UtcNow);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "instance.json"),
                JsonSerializer.Serialize(instance, JsonOptions), cancellationToken);
            return instance;
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }

    public void Delete(string name)
    {
        ValidateName(name);
        var root = Path.GetFullPath(Path.Combine(_root, "instances"));
        var directory = Path.GetFullPath(Path.Combine(root, name));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Instance path escaped the instances directory.");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Use a valid instance name without path separators.", nameof(name));
    }
}
