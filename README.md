# Nour Launcher

A Windows-first Minecraft Java Edition launcher built with C#/.NET and WPF.

## Goals
- Discover official Minecraft versions from Mojang metadata.
- Install and launch independent Minecraft instances.
- Manage compatible Java runtimes without relying on system Java.
- Cache game files for eligible offline play after legitimate authentication.
- Manage Fabric, Forge, NeoForge, Quilt, and mods.
- Build Windows artifacts through GitHub Actions.

## Requirements
- Windows 10/11 x64 for the initial release.
- .NET 10 SDK to build from source.
- No separately installed Java is required to run Nour Launcher.

## Repository layout
- src/NourLauncher.App: WPF desktop application and MVVM UI.
- src/NourLauncher.Core: shared contracts and domain models.
- src/NourLauncher.Minecraft: Mojang metadata and game installation logic.
- src/NourLauncher.Runtimes: managed Java runtime resolution.
- src/NourLauncher.Downloads: download and integrity infrastructure.
- src/NourLauncher.Storage: local configuration and persistence.
- tests: automated tests.

## Development status
Early scaffold. The current milestone is the application shell and reliable version metadata discovery. Installation, authentication, runtime provisioning, mod loaders, and offline validation will be implemented in subsequent milestones.

## Account and offline behavior
Nour does not bypass Minecraft account ownership or Microsoft authentication. It may support cached offline play when the user has previously authenticated and the account/session remains eligible. Local development profiles are for launcher UI and test workflows, not an ownership or authentication bypass.

## Build
Open NourLauncher.sln in Visual Studio 2026 or build with the .NET 10 SDK:

    dotnet restore NourLauncher.sln
    dotnet build NourLauncher.sln -c Release

## License
See LICENSE.