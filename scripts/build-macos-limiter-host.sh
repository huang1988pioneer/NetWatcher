#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
project="$repo_root/macos-limiter/Xcode/NetWatcherLimiterHost.xcodeproj"
derived_data="$repo_root/macos-limiter/.xcode-derived"

xcodebuild \
  -project "$project" \
  -scheme NetWatcherLimiterHost \
  -configuration Release \
  -derivedDataPath "$derived_data" \
  build

echo "$derived_data/Build/Products/Release/NetWatcherLimiterHost.app"
