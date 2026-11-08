using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NourLauncher.Core.Accounts;

/// <summary>
/// Offline ("no account") player identity.
/// <para>
/// Minecraft derives a local player's UUID from the name itself when there is no
/// Microsoft session. The derivation is a name-based version 3 UUID over
/// <c>"OfflinePlayer:" + username</c>, which is what the Java client does internally
/// (see the vanilla launcher's <c>UUID.nameUUIDFromBytes</c>). Reproducing it exactly
/// means a given name keeps the same UUID every launch, so worlds, player data and
/// LAN identities stay stable instead of resetting each time the game starts.
/// </para>
/// </summary>
public static class OfflineAccount
{
    /// <summary>Minecraft's local name prefix for offline UUID derivation.</summary>
    public const string UuidNamePrefix = "OfflinePlayer:";

    /// <summary>Vanilla-compatible rule for local player names.</summary>
    public static readonly Regex ValidUsername = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    /// <summary>
    /// Returns the deterministic offline UUID for <paramref name="username"/> in the
    /// canonical 8-4-4-4-12 form, identical to the one the Java client would produce.
    /// </summary>
    public static string ComputeUuid(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("An offline player name is required.", nameof(username));

        var hash = MD5.HashData(Encoding.UTF8.GetBytes(UuidNamePrefix + username));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3 (name based, MD5)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant

        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>True when <paramref name="username"/> is a name the vanilla client accepts locally.</summary>
    public static bool IsValidUsername(string? username) =>
        !string.IsNullOrWhiteSpace(username) && ValidUsername.IsMatch(username);
}
