namespace NourLauncher.Core.Models;

/// <summary>
/// A locally stored launcher profile. Local profiles are for offline development/testing
/// and do not represent Microsoft authentication or Minecraft ownership.
/// </summary>
public sealed record LauncherProfile(
    Guid Id,
    string Username,
    DateTimeOffset CreatedAt,
    bool IsActive = false);
