# Long-Term macOS Limiter Plan

Goal: turn the Swift companion foundation into a real NetLimiter-style macOS component.

## Phase 1: Native Host App

- Create `NetWatcherLimiterHost.app` in Xcode.
- Embed the `NetWatcherFilter.appex` Network Extension target.
- Use `NEFilterManager` to save and enable the filter configuration. The reusable Swift wrapper now exists in `NetWatcherLimiterHostSupport`.
- Store rules in the app group container:
  - `group.com.huang1988pioneer.netwatcher/NetWatcherLimiter/rules.json`

## Phase 2: App Identity Resolution

The macOS SDK does not expose `NEFilterFlow.sourceAppIdentifier`; it is unavailable on macOS.

Use these instead:

- `sourceAppAuditToken`
- `sourceProcessAuditToken`

Then resolve audit token to:

- pid
- executable path
- code signing identifier
- bundle identifier when available

The final UI-facing key should prefer bundle id, with executable path fallback.

## Phase 3: XPC Bridge

Add an XPC service between the Avalonia app and the native host:

- Avalonia sends limit rules by process/bundle id.
- Native host validates and writes app-group rules.
- Filter extension reloads rules from the app-group file.
- The initial Swift protocol/service exists in `NetWatcherLimiterXPC`; the next step is packaging it inside the signed host app and calling it from .NET.

Protocol shape:

```json
{
  "bundleIdentifier": "com.google.Chrome",
  "inboundBytesPerSecond": 1048576,
  "outboundBytesPerSecond": 262144,
  "isEnabled": true
}
```

## Phase 4: Real Flow Shaping

Current prototype uses `passBytes` and `pause()` decisions.

Production work:

- Track per-flow inbound and outbound byte offsets.
- Maintain per-app token buckets.
- Resume paused TCP flows only when enough tokens are available.
- Keep UDP pause below macOS timeout limits.
- Add flow cleanup when flows close.

## Phase 5: Release Requirements

- Apple Developer account with Network Extension entitlement.
- App group entitlement.
- Developer ID signing.
- Notarization.
- User-facing installer flow for enabling Network Extension.

## Risk

If Apple does not grant the required entitlement, the app can still ship macOS monitoring, but not real per-app limiting.
