#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "Usage: $0 <osx-arm64|osx-x64> [version]" >&2
  exit 64
fi

rid="$1"
version="${2:-1.2.11}"

case "$rid" in
  osx-arm64|osx-x64) ;;
  *)
    echo "Unsupported runtime identifier: $rid" >&2
    echo "Expected osx-arm64 or osx-x64" >&2
    exit 64
    ;;
esac

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
platform="${rid/osx-/macos-}"
release_dir="$repo_root/artifacts/release/v$version/$platform"
publish_dir="$repo_root/artifacts/release/v$version/$platform-publish"
app_dir="$release_dir/NetWatcher.app"
contents_dir="$app_dir/Contents"
macos_dir="$contents_dir/MacOS"
zip_path="$repo_root/artifacts/release/NetWatcher-v$version-$platform.zip"

rm -rf "$publish_dir" "$release_dir" "$zip_path"
mkdir -p "$publish_dir" "$macos_dir" "$contents_dir/Resources" "$(dirname "$zip_path")"

dotnet publish "$repo_root/NetWatcher.App.csproj" \
  -c Release \
  -r "$rid" \
  --self-contained true \
  -o "$publish_dir"

cp -R "$publish_dir"/. "$macos_dir/"

/usr/libexec/PlistBuddy \
  -c "Add :CFBundleIdentifier string com.huang1988pioneer.netwatcher" \
  -c "Add :CFBundleName string NetWatcher" \
  -c "Add :CFBundleDisplayName string NetWatcher" \
  -c "Add :CFBundleExecutable string NetWatcher.App" \
  -c "Add :CFBundlePackageType string APPL" \
  -c "Add :CFBundleVersion string $version" \
  -c "Add :CFBundleShortVersionString string $version" \
  -c "Add :LSMinimumSystemVersion string 11.0" \
  -c "Add :NSHighResolutionCapable bool true" \
  "$contents_dir/Info.plist"

chmod +x "$macos_dir/NetWatcher.App"
xattr -cr "$app_dir" 2>/dev/null || true
codesign --force --deep --sign - "$app_dir"

(
  cd "$release_dir"
  ditto -c -k --sequesterRsrc --keepParent "NetWatcher.app" "$zip_path"
)

echo "$zip_path"
