# Sky Client

A Windows-first Minecraft Java Edition launcher built with C#/.NET and WPF.

## Goals

- Browse official Minecraft releases, snapshots, old beta and alpha versions from Mojang metadata.
- Install version metadata, client jars, Windows libraries, native archives, and assets with SHA-1 verification.
- Read each version's declared Java major and provision an isolated Eclipse Temurin runtime automatically (legacy fallback for older metadata).
- Install and launch Minecraft versions — offline with a local profile today, through a Microsoft session once authentication lands.
- Install and manage private Eclipse Temurin Java 25 alongside version-compatible runtimes; no system Java dependency.
- Manage isolated instances and Modrinth mods.
- Install Fabric and Quilt loader profiles; Forge and NeoForge installers are not wired yet.
- Build Windows artifacts through GitHub Actions.

## Requirements

- Windows 10/11 x64 for the initial release.
- .NET 10 SDK to build from source.
- No separately installed Java is required to run Sky Client.

## Repository layout

- `src/NourLauncher.App`: WPF desktop application and MVVM UI.
- `src/NourLauncher.Core`: shared contracts and domain models.
- `src/NourLauncher.Minecraft`: Mojang metadata, installation, and launch argument construction.
- `src/NourLauncher.Runtimes`: managed Java runtime resolution.
- `src/NourLauncher.Downloads`: download and integrity infrastructure.
- `src/NourLauncher.Storage`: local configuration and persistence.
- `tests`: automated tests (planned).

## Development status

Early alpha. The WPF shell includes profiles, official version browsing and installation, automatic Java management, isolated instances, Modrinth mod search/install/enable/disable/remove, Fabric and Quilt profile installation, Microsoft device-code sign-in, and local crash-log analysis. The launch engine resolves inherited metadata, locates the base client jar, builds the classpath, expands modern and legacy arguments, and starts the Java process. Crash analysis stays local. Authenticated sessions are currently held in memory, so users must sign in again after restarting Sky Client. Forge and NeoForge installers, CurseForge integration, dependency resolution for mods, and full launch-process monitoring remain unfinished.

## Accounts and offline mode

Sky Client has two launch paths.

### Offline mode (no account)

Disabled by default and enabled under **Settings → Enable offline mode**. Minecraft then starts with a locally generated player identity instead of a signed-in account:

- the player UUID is Minecraft's own name-based offline UUID (MD5 over `OfflinePlayer:<name>`, version 3), so a given name always maps to the same world data and LAN identity;
- no access token is used and the client is started with `--userType legacy`;
- the session is deliberately marked offline and can never be confused with an authenticated one.

This path works for **singleplayer worlds** and **LAN games**. It does not authenticate, does not verify Minecraft ownership, and gives no skins, capes, Realms, account sync, or access to servers that require authentication — those servers reject the connection. A legitimate Minecraft licence is still required to play.

### Microsoft / online

Microsoft sign-in uses the device-code flow and then validates the Xbox Live, XSTS, and Minecraft Services session. Sky Client requires a Microsoft public-client application ID configured under **Settings**; no client secret is embedded. The account must have a Minecraft Java profile. Authenticated tokens are currently memory-only and are not written to plain JSON.

## Build

Open `NourLauncher.sln` in Visual Studio 2026 or build with the .NET 10 SDK:

    dotnet restore NourLauncher.sln
    dotnet build NourLauncher.sln -c Release

## License

See LICENSE.
