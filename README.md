# PalPeek

PalPeek is a private, view-only Steam game watcher for small Tailscale
networks. It detects a running Steam game, publishes its availability to
friends in the same tailnet, and launches a bundled Sunshine/Moonlight stream.

## MVP constraints

- Windows 11 x64 and Steam games only
- Native client, H.264, 720p60/4 Mbps or 1080p60/8 Mbps
- Maximum three simultaneous viewers
- Game-window and game-process-tree audio only
- No remote keyboard, mouse, touch, or controller input

## Layout

- `src/PalPeek.Core` — Steam/Tailscale discovery, leases and API contracts
- `src/PalPeek.App` — WPF UI, tray app, peer API and viewer orchestration
- `third_party/Sunshine` — pinned Sunshine fork with PalPeek capture/audio changes
- `installer` — Windows packaging
- `packaging/sunshine` — locked-down host configuration
- `scripts` — reproducible release staging
- `tests/PalPeek.Core.Tests` — unit tests

## Build

Install .NET SDK 8.0.423, then run:

```powershell
dotnet restore PalPeek.sln
dotnet build PalPeek.sln -c Release
dotnet test PalPeek.sln -c Release
```

Runtime executables are expected at `runtime/sunshine/sunshine.exe` and
`runtime/moonlight/moonlight.exe`. Tailscale is installed separately.

The Sunshine fork must be built from tag `v2026.516.143833`; Moonlight PC
portable is pinned to `v6.1.0`. Run `scripts/build-release.ps1` after both
runtime components are available, then compile `installer/PalPeek.iss` with
Inno Setup 6.
