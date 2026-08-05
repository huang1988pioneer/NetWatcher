# NetWatcher macOS Limiter

This folder contains the native macOS companion used for per-app speed limiting.

Current state:

- `NetWatcherLimiterCore` contains rules, a token bucket, and per-app flow limiter logic.
- `NetWatcherFilterExtension` contains an `NEFilterDataProvider` entry point and a macOS-safe flow identity layer.
- `NetWatcherLimiterHostSupport` contains the host-side `NEFilterManager` wrapper and rule writer.
- `NetWatcherLimiterXPC` contains an optional XPC protocol/service skeleton.
- `Xcode/NetWatcherLimiterHost.xcodeproj` builds `NetWatcherLimiterHost.app` and embeds `NetWatcherFilter.appex`.
- `NetWatcherLimiterHost.app` accepts one JSON command on stdin, so the Avalonia app can request rule changes without direct App Group access.
- `Entitlements/` and `Templates/` contain the values needed when these sources are added to Xcode host app and Network Extension targets.

Important limitations:

- Real installation requires Apple Network Extension entitlement approval and a signed/notarized host app.
- The Avalonia app can monitor process traffic today; it enables macOS limit controls only after the signed host app is bundled beside it.
- In the macOS 26.2 SDK, `NEFilterFlow.sourceAppIdentifier` is unavailable on macOS. The provider resolves the audit token through the Security framework and uses the code-signing identifier as the rule key.
- The host manager enables `filterSockets`, which is the supported macOS content-filter path. `filterBrowsers` is intentionally not used on macOS.

Expected Xcode targets:

1. `NetWatcherLimiterHost.app`
   - Signed native command host invoked by the Avalonia bridge.
   - Uses `NEFilterManager` to install and enable the filter provider.
   - Shares `rules.json` through `group.com.huang1988pioneer.netwatcher`.

2. `NetWatcherFilter.appex`
   - Network Extension target using `com.apple.networkextension.filter-data`.
   - Principal class: `FilterDataProvider`.
   - Uses `NetWatcherLimiterCore` and the shared app-group rules file.

3. Avalonia bridge
   - The desktop UI passes PID plus selected limits to the host app.
   - The host resolves PID to bundle ID, writes the app-group rule and enables the filter.

Local validation:

```bash
cd macos-limiter
swift build
swift run netwatcher-limiter-diagnostics

# Builds the app bundle with its embedded Filter extension.
../scripts/build-macos-limiter-host.sh
```

Long-term work:

- Add measured per-flow queues and flow statistics exposed to the UI.
- Sign with an Apple Developer profile containing the Network Extension entitlement, then notarize the combined release.
