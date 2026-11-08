using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NourLauncher.Minecraft.Authentication;

public sealed record MicrosoftDeviceCode(
    string DeviceCode, string UserCode, string VerificationUri,
    int ExpiresIn, int Interval, string Message);

/// <summary>
/// Microsoft device-code sign-in followed by Xbox Live, XSTS and Minecraft Services.
/// Requires a public-client application ID registered by the launcher publisher.
/// No client secret is embedded or requested.
/// </summary>
public sealed class MicrosoftMinecraftAuthService
{
    private readonly HttpClient _http;

    public MicrosoftMinecraftAuthService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<MicrosoftDeviceCode> BeginSignInAsync(
        string clientId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(clientId, out _))
            throw new ArgumentException("Enter a valid Microsoft public-client application ID.", nameof(clientId));

        using var response = await _http.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = "XboxLive.signin offline_access"
            }), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadOAuthError(body));
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new MicrosoftDeviceCode(
            root.GetProperty("device_code").GetString()!,
            root.GetProperty("user_code").GetString()!,
            root.GetProperty("verification_uri").GetString()!,
            root.GetProperty("expires_in").GetInt32(),
            root.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5,
            root.TryGetProperty("message", out var message) ? message.GetString()! : "Complete sign-in in your browser.");
    }

    public async Task<Launching.MinecraftLaunchSession> CompleteSignInAsync(
        string clientId, MicrosoftDeviceCode deviceCode,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        using var oauth = await PollForMicrosoftTokenAsync(clientId, deviceCode, progress, cancellationToken);
        var microsoftAccessToken = oauth.GetProperty("access_token").GetString()!;
        progress?.Report("Verifying Xbox Live account…");

        var xbox = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoftAccessToken },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        }, cancellationToken);

        var xboxToken = xbox.RootElement.GetProperty("Token").GetString()!;
        var xuid = xbox.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;

        progress?.Report("Checking Xbox security token…");
        var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xboxToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        }, cancellationToken);

        var xstsToken = xsts.RootElement.GetProperty("Token").GetString()!;
        var userHash = xsts.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;

        progress?.Report("Requesting Minecraft access…");
        using var minecraftResponse = await _http.PostAsJsonAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" }, cancellationToken);
        var minecraftBody = await minecraftResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!minecraftResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadMinecraftError(minecraftBody));
        using var minecraft = JsonDocument.Parse(minecraftBody);
        var minecraftToken = minecraft.RootElement.GetProperty("access_token").GetString()!;

        progress?.Report("Checking Minecraft Java profile and game entitlement…");
        using var profileRequest = new HttpRequestMessage(HttpMethod.Get,
            "https://api.minecraftservices.com/minecraft/profile");
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftToken);
        using var profileResponse = await _http.SendAsync(profileRequest, cancellationToken);
        var profileBody = await profileResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!profileResponse.IsSuccessStatusCode)
        {
            if ((int)profileResponse.StatusCode == 404)
                throw new InvalidOperationException("This Microsoft account does not have a Minecraft Java profile.");
            throw new InvalidOperationException(ReadMinecraftError(profileBody));
        }

        using var profile = JsonDocument.Parse(profileBody);
        var id = profile.RootElement.GetProperty("id").GetString()!;
        var name = profile.RootElement.GetProperty("name").GetString()!;
        progress?.Report($"Signed in as {name}.");
        return new Launching.MinecraftLaunchSession(
            name, id, minecraftToken, UserType: "msa", Xuid: xuid);
    }

    private async Task<JsonDocument> PollForMicrosoftTokenAsync(
        string clientId, MicrosoftDeviceCode code, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var interval = Math.Max(5, code.Interval);
        while (DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(code.ExpiresIn))
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            using var response = await _http.PostAsync(
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = clientId,
                    ["device_code"] = code.DeviceCode
                }), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode) return JsonDocument.Parse(body);
            using var error = JsonDocument.Parse(body);
            var errorCode = error.RootElement.TryGetProperty("error", out var value) ? value.GetString() : "";
            if (errorCode == "authorization_pending") continue;
            if (errorCode == "slow_down") { interval += 5; continue; }
            if (errorCode == "authorization_declined")
                throw new InvalidOperationException("Microsoft sign-in was declined.");
            if (errorCode == "expired_token")
                throw new InvalidOperationException("The Microsoft device code expired. Start sign-in again.");
            throw new InvalidOperationException(ReadOAuthError(body));
        }
        throw new TimeoutException("Microsoft sign-in timed out. Start sign-in again.");
    }

    private async Task<JsonDocument> PostJsonAsync(string url, object payload, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync(url, payload, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadXboxError(body));
        return JsonDocument.Parse(body);
    }

    private static string ReadOAuthError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error_description", out var description)
                ? description.GetString() ?? "Microsoft sign-in failed."
                : "Microsoft sign-in failed.";
        }
        catch { return "Microsoft sign-in failed. The identity service returned an invalid response."; }
    }

    private static string ReadXboxError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("XErr", out var error))
                return "Xbox authentication failed (code " + error.GetRawText() + "). Check account eligibility and Xbox profile settings.";
        }
        catch { }
        return "Xbox authentication failed. Check the Microsoft account's Xbox profile and try again.";
    }

    private static string ReadMinecraftError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errorMessage", out var message))
                return message.GetString() ?? "Minecraft authentication failed.";
            if (document.RootElement.TryGetProperty("error", out var error))
                return error.GetString() ?? "Minecraft authentication failed.";
        }
        catch { }
        return "Minecraft authentication failed. The account may not have Java Edition access.";
    }
}
