using System.Text.Json.Serialization;

namespace NourLauncher.Core.Models;

/// <summary>
/// How a launcher profile's identity is produced. Offline profiles use a locally
/// derived Minecraft player identity and need no account; Microsoft profiles are
/// reserved for the real authentication flow.
/// </summary>
public enum LauncherAccountType
{
    Offline = 0,
    Microsoft = 1
}

/// <summary>
/// A locally stored launcher profile. Offline profiles are local player identities:
/// they are not Microsoft accounts and do not prove Minecraft ownership, so they can
/// only be used for singleplayer and LAN play (see <see cref="LauncherAccountType"/>).
/// </summary>
public sealed record LauncherProfile(
    Guid Id,
    string Username,
    DateTimeOffset CreatedAt,
    bool IsActive = false,
    LauncherAccountType AccountType = LauncherAccountType.Offline)
{
    /// <summary>Display name of the account type, shown in the profile list.</summary>
    [JsonIgnore]
    public string AccountTypeLabel => AccountType == LauncherAccountType.Offline
        ? "Offline account"
        : "Microsoft account";
}
