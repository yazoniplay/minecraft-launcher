using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace NourLauncher.Minecraft.Authentication;

/// <summary>Authenticates an Ely.by account through its documented Yggdrasil API.</summary>
public sealed class ElyByAuthenticationService
{
    private static readonly Uri AuthEndpoint = new("https://authserver.ely.by/auth/authenticate");
    private readonly HttpClient _http;
    private readonly string _root;

    public ElyByAuthenticationService(HttpClient? httpClient = null, string? rootDirectory = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SkyClient/0.3 (https://github.com/yazoniplay/minecraft-launcher)");
        _root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyClient");
    }

    public async Task<Launching.MinecraftLaunchSession> AuthenticateAsync(
        string usernameOrEmail, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail))
            throw new ArgumentException("Enter your Ely.by username or email.", nameof(usernameOrEmail));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Enter your Ely.by password.", nameof(password));

        var clientToken = Guid.NewGuid().ToString("N");
        using var response = await _http.PostAsJsonAsync(AuthEndpoint, new
        {
            username = usernameOrEmail.Trim(),
            password,
            clientToken,
            requestUser = true
        }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadAuthError(body, response.StatusCode));

        using var document = JsonDocument.Parse(body);
        var selected = document.RootElement.GetProperty("selectedProfile");
        var name = selected.GetProperty("name").GetString()!;
        var uuid = selected.GetProperty("id").GetString()!;
        var token = document.RootElement.GetProperty("accessToken").GetString()!;
        var injector = await EnsureAuthlibInjectorAsync(cancellationToken);

        return new Launching.MinecraftLaunchSession(
            name, uuid, token, UserType: "ely", ClientId: clientToken,
            AuthlibInjectorPath: injector);
    }

    private async Task<string> EnsureAuthlibInjectorAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_root, "authlib-injector");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "authlib-injector.jar");
        if (File.Exists(destination))
        {
            var info = new FileInfo(destination);
            if (info.Length > 100_000) return destination;
        }

        using var metadataResponse = await _http.GetAsync(
            "https://authlib-injector.yushi.moe/artifact/latest.json", cancellationToken);
        metadataResponse.EnsureSuccessStatusCode();
        using var metadata = JsonDocument.Parse(
            await metadataResponse.Content.ReadAsStringAsync(cancellationToken));
        var downloadUrl = metadata.RootElement.GetProperty("download_url").GetString()!;
        var expectedHash = metadata.RootElement.GetProperty("checksums").GetProperty("sha256").GetString();
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The authlib-injector API returned an invalid download URL.");

        var temporary = destination + ".download";
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(target, cancellationToken);

            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                await using var stream = File.OpenRead(temporary);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("authlib-injector SHA-256 verification failed.");
            }
            if (new FileInfo(temporary).Length < 100_000)
                throw new InvalidDataException("The downloaded authlib-injector file is unexpectedly small.");
            File.Move(temporary, destination, true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ReadAuthError(string body, System.Net.HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errorMessage", out var message))
                return message.GetString() ?? $"Ely.by sign-in failed ({(int)status}).";
            if (document.RootElement.TryGetProperty("error", out var error))
                return error.GetString() ?? $"Ely.by sign-in failed ({(int)status}).";
        }
        catch { }
        return $"Ely.by sign-in failed ({(int)status}). Check the credentials and try again.";
    }
}
