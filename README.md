# Sky Client

A Windows-first Minecraft Java Edition launcher built with C#/.NET and WPF.

## Goals
- Browse official Minecraft releases, snapshots, old beta and alpha versions from Mojang metadata.\n- Install version metadata, client jars, Windows libraries, native archives, and assets with SHA-1 verification.
- Install and launch independent Minecraft instances.
- Install and manage private Eclipse Temurin Java 25 alongside version-compatible runtimes; no system Java dependency.
- Cache game files for eligible offline play after legitimate authentication.
- Manage Fabric, Forge, NeoForge, Quilt, and mods.
- Build Windows artifacts through GitHub Actions.

## Requirements
- Windows 10/11 x64 for the initial release.
- .NET 10 SDK to build from source.
- No separately installed Java is required to run Sky Client.

## Repository layout
- src/NourLauncher.App: WPF desktop application and MVVM UI.
- src/NourLauncher.Core: shared contracts and domain models.
- src/NourLauncher.Minecraft: Mojang metadata and game installation logic.
- src/NourLauncher.Runtimes: managed Java runtime resolution.
- src/NourLauncher.Downloads: download and integrity infrastructure.
- src/NourLauncher.Storage: local configuration and persistence.
- tests: automated tests.

## Development status
Early alpha. The WPF shell includes persistent local profile management, a managed Eclipse Temurin Java 25 installer, and an official Minecraft version library. The version installer downloads Mojang metadata, the client jar, applicable Windows libraries and native archives, plus indexed assets, verifying SHA-1 hashes where provided. Microsoft authentication, version-aware Java selection, game launching, mod loaders, and authenticated offline launch are still in progress.

## Account and offline behavior
Sky Client does not bypass Minecraft account ownership or Microsoft authentication. It may support cached offline play when the user has previously authenticated and the account/session remains eligible. Local profiles are stored on this PC and currently identify a launcher UI profile only. They are not Microsoft accounts and cannot be used to bypass Minecraft ownership or server authentication. Authenticated offline play remains a separate feature.

## Build
Open NourLauncher.sln in Visual Studio 2026 or build with the .NET 10 SDK:

    dotnet restore NourLauncher.sln
    dotnet build NourLauncher.sln -c Release

## License
See LICENSE.