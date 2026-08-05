# NetWatcher · 網路速度監控器

Cross-platform desktop app for real-time network bandwidth monitoring and per-process speed limits.
Windows builds include per-process traffic via ETW network events.

## Download

Latest release:

- [NetWatcher v1.2.10](https://github.com/huang1988pioneer/NetWatcher/releases/tag/v1.2.10)

Release assets:

- `NetWatcher-v1.2.10-win-x64.zip`
- `NetWatcher-v1.2.10-macos-arm64.zip`
- `NetWatcher-v1.2.10-macos-x64.zip`
- `NetWatcher-v1.2.10-linux-x64.zip`
- `NetWatcher-v1.2.10-linux-arm64.zip`

## Features

Dashboard inspired by modern bandwidth monitors:

| Area | Capability |
|------|------------|
| **總覽** | Dual download / upload speed cards with 60s sparklines (MB/s) |
| **程式列表** | Live per-process DL/UL rates, status, avatar |
| **速度限制** | Per-app download & upload MB/s presets, free-form MB/s input, enable toggle |
| **網路監控** | Larger dual-channel history charts + averages |
| **歷史記錄** | Session / Today / Yesterday / Week / Month / All-time volume |
| **設定** | Adapter picker, theme skins, CSV export, clear limits |

### Per-process speed limit

In the process table:

1. Choose **下載限制** / **上傳限制** (presets, `自訂…`, or type any MB/s below the dropdown)
2. Keep the **toggle** on to apply control; turn off to release
3. Limits persist in `settings/limits.json`

### Windows control notes

- **Download & upload limit**: WinDivert packet shaping per process (token/virtual-clock rate, target MB/s with small burst tolerance)
- **Upload limit (extra)**: Policy-based QoS (`New-NetQosPolicy`) when elevated
- **Block / clear**: Windows Firewall rules where applicable
- Run as **Administrator** for ETW process traffic, WinDivert shaping, QoS, and Firewall actions
- Without admin, speed limits cannot actually enforce (status will say so)

### Appearance skins

Settings → 外觀主題:

- 整合 · 參考儀表板 (default)
- NetBalancer / BWMeter / Eltrafico / GlassWire / NetLimiter 配色

### Persistence

- Process limits → `settings/limits.json`
- Daily traffic counters → `settings/traffic-stats.json`
- CSV export → `exports/`

## Requirements

- Windows 10/11, macOS Apple Silicon / Intel, or Linux x64 / arm64
- Administrator permission on Windows for per-process ETW + packet speed limits

## Quick Start

1. Download the zip for your platform from the latest release.
2. Extract the zip.
3. On Windows, run `NetWatcher.App.exe` as administrator.
4. On macOS / Linux, run `chmod +x NetWatcher.App`, then start `./NetWatcher.App`.
5. Watch live speed cards; set per-process limits from the table (Windows).

## Development

Build locally:

```powershell
dotnet build NetWatcher.App.csproj -c Release
```

Publish Windows release:

```powershell
dotnet publish NetWatcher.App.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\release\v1.2.10\win-x64
Compress-Archive -Path .\artifacts\release\v1.2.10\win-x64\* -DestinationPath .\artifacts\release\NetWatcher-v1.2.10-win-x64.zip -Force
```

Publish macOS releases:

```powershell
dotnet publish NetWatcher.App.csproj -c Release -r osx-arm64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\release\v1.2.10\macos-arm64
dotnet publish NetWatcher.App.csproj -c Release -r osx-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\release\v1.2.10\macos-x64
Compress-Archive -Path .\artifacts\release\v1.2.10\macos-arm64\* -DestinationPath .\artifacts\release\NetWatcher-v1.2.10-macos-arm64.zip -Force
Compress-Archive -Path .\artifacts\release\v1.2.10\macos-x64\* -DestinationPath .\artifacts\release\NetWatcher-v1.2.10-macos-x64.zip -Force
```

Publish Linux releases:

```powershell
dotnet publish NetWatcher.App.csproj -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\release\v1.2.10\linux-x64
dotnet publish NetWatcher.App.csproj -c Release -r linux-arm64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\release\v1.2.10\linux-arm64
Compress-Archive -Path .\artifacts\release\v1.2.10\linux-x64\* -DestinationPath .\artifacts\release\NetWatcher-v1.2.10-linux-x64.zip -Force
Compress-Archive -Path .\artifacts\release\v1.2.10\linux-arm64\* -DestinationPath .\artifacts\release\NetWatcher-v1.2.10-linux-arm64.zip -Force
```

## Notes

- Per-process traffic uses Windows ETW network events (~1s refresh).
- Dashboard rates auto-scale (**B/s · KB/s · MB/s**); limits use binary megabytes/s (1 MB/s = 1024 KB/s).
- Totals use **MB / GB** volume units.
- macOS and Linux builds show total bandwidth; per-process traffic and packet limits are Windows-only.
- Not affiliated with NetBalancer, Eltrafico, BWMeter, GlassWire, or NetLimiter.
