# NetWatcher

Cross-platform desktop app for monitoring total network bandwidth in real time.
Windows builds also include per-process traffic through ETW network events.

## Download

Latest release:

- [NetWatcher v1.1.0](https://github.com/goldshoot0720/mynetwatcher/releases/tag/v1.1.0)

Release assets:

- `NetWatcher-v1.1.0-win-x64.zip`
- `NetWatcher-v1.1.0-macos-arm64.zip`
- `NetWatcher-v1.1.0-macos-x64.zip`

## Features

- Real-time total download and upload speed
- Traffic history chart for recent bandwidth changes
- Per-process traffic list with search and sorting on Windows
- CSV export for captured traffic history
- Responsive layout optimized for both laptop and desktop screens

## Requirements

- Windows 10/11, macOS Apple Silicon, or macOS Intel
- Administrator permission on Windows for per-process ETW monitoring

## Quick Start

1. Download the zip for your platform from the latest release.
2. Extract the zip.
3. On Windows, run `NetWatcher.App.exe` as administrator.
4. On macOS, run `chmod +x NetWatcher.App`, then start `./NetWatcher.App`.
5. Watch live traffic, filter processes when supported, and export CSV when needed.

## Development

Build locally:

```powershell
dotnet build NetWatcher.App.csproj -c Release
```

Publish Windows release:

```powershell
dotnet publish NetWatcher.App.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\release\win-x64
Compress-Archive -Path .\artifacts\release\win-x64\* -DestinationPath .\artifacts\release\NetWatcher-v1.1.0-win-x64.zip -Force
```

Publish macOS releases:

```powershell
dotnet publish NetWatcher.App.csproj -c Release -r osx-arm64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\release\macos-arm64
dotnet publish NetWatcher.App.csproj -c Release -r osx-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\release\macos-x64
Compress-Archive -Path .\artifacts\release\macos-arm64\* -DestinationPath .\artifacts\release\NetWatcher-v1.1.0-macos-arm64.zip -Force
Compress-Archive -Path .\artifacts\release\macos-x64\* -DestinationPath .\artifacts\release\NetWatcher-v1.1.0-macos-x64.zip -Force
```

Create GitHub release:

```powershell
gh release create v1.1.0 .\artifacts\release\NetWatcher-v1.1.0-win-x64.zip .\artifacts\release\NetWatcher-v1.1.0-macos-arm64.zip .\artifacts\release\NetWatcher-v1.1.0-macos-x64.zip --title "NetWatcher v1.1.0" --notes "Adds macOS arm64/x64 release packages while keeping Windows ETW per-process monitoring."
```

## Notes

- The app uses Windows ETW network events for per-process traffic monitoring on Windows.
- macOS builds show total network bandwidth. Per-process traffic is shown as unsupported on macOS.
- Exported CSV files are written to the `exports` folder.
