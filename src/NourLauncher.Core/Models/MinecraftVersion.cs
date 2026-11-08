namespace NourLauncher.Core.Models;

public sealed record MinecraftVersion(
    string Id,
    string Type,
    DateTimeOffset ReleaseTime,
    Uri MetadataUrl,
    string? Sha1,
    bool IsInstalled = false);