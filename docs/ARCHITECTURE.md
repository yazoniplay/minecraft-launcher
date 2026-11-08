# Sky Client Architecture

## Layer boundaries
- App: WPF presentation, navigation, view models, and user interaction. It must not own Minecraft installation rules.
- Core: shared domain models, result types, and service contracts.
- Minecraft: Mojang metadata parsing, version resolution, asset/library installation, launch argument construction, and process lifecycle.
- Runtimes: Java requirement resolution, runtime manifests, provisioning, validation, and selection.
- Downloads: bounded concurrency, retries, resumable transfers, cache keys, and checksum verification.
- Storage: local settings, SQLite repositories, instance metadata, and secure account-token storage integration.

## Key invariants
1. An instance has an independent game directory and configuration.
2. A version is launchable only when all required artifacts and a compatible runtime are present.
3. Metadata is authoritative; do not maintain a hardcoded version-to-Java mapping.
4. Downloads are verified against metadata when a trusted checksum is provided.
5. Offline launch validates local files first and never silently attempts a network fetch.
6. A launch session is either authenticated or explicitly offline. An offline session (`IsOffline`) is derived locally, carries no access token, and is never treated as proof of entitlement.
7. Offline mode is opt-in, persisted in `settings.json`, and gated behind a one-time confirmation; it cannot be enabled implicitly by a launch call.
8. UI code calls application services; it does not construct raw game classpaths itself.

## Installation state machine
NotInstalled -> Queued -> Downloading -> Verifying -> Ready

Recoverable failure returns to Queued or Downloading using already verified cache entries. A corrupted artifact is quarantined and fetched again when online.

## Security
- Use the operating system credential store for refresh tokens; never write credentials to plain JSON or logs.
- Treat mod archives and metadata as untrusted input. Validate paths before extraction to prevent archive traversal.
- Restrict downloads to expected HTTPS sources and validate redirects.
- Do not fabricate authenticated sessions. Offline identities are local-only: `MinecraftLaunchSession.CreateOffline` derives the vanilla offline UUID, uses no token, and is rejected by anything that requires a verified account.

## Offline launch path
1. The user enables offline mode in Settings (one-time warning) and selects a local profile.
2. `MinecraftLaunchSession.CreateOffline(name)` derives the UUID from the name and marks the session `IsOffline`.
3. `MinecraftLaunchService` validates the session, resolves metadata, the classpath and the managed runtime, then starts the process.
4. Invalid names (not 3–16 of `[A-Za-z0-9_]`) are rejected before any process starts, because the name becomes both a UUID input and a directory name.

## Planned milestones
1. WPF shell and CI.
2. Mojang manifest and version metadata catalogue.
3. Download engine and vanilla installation.
4. Runtime metadata and managed Java provisioning.
5. Correct launch argument resolution and process monitoring.
6. Microsoft authentication and online launch (offline launch mode is available now).
7. Instances, loaders, mod providers, and dependency resolution.
8. Repair, export/import, diagnostics, packaging, and release hardening.
