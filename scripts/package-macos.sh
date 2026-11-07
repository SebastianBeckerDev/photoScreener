#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
runtime="${RUNTIME:-osx-arm64}"
if [[ "$runtime" != "osx-arm64" ]]; then echo 'Only the Apple Silicon profile is defined.' >&2; exit 1; fi
publish="$root/artifacts/publish/$runtime"
app="$root/artifacts/Photo Screener.app"
dotnet publish "$root/src/PhotoScreener.Desktop/PhotoScreener.Desktop.csproj" -c Release -r "$runtime" --self-contained true -p:RestoreLockedMode=true --disable-build-servers -o "$publish"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
rsync -a "$publish/" "$app/Contents/MacOS/"
cp "$root/packaging/macos/Info.plist" "$app/Contents/Info.plist"
chmod +x "$app/Contents/MacOS/PhotoScreener.Desktop"
plutil -lint "$app/Contents/Info.plist"
if [[ -n "${SIGNING_IDENTITY:-}" ]]; then
  while IFS= read -r -d '' native; do
    codesign --force --timestamp --options runtime --sign "$SIGNING_IDENTITY" "$native"
  done < <(find "$app/Contents/MacOS" -type f -name '*.dylib' -print0)
  if [[ -f "$app/Contents/MacOS/createdump" ]]; then
    chmod +x "$app/Contents/MacOS/createdump"
    codesign --force --timestamp --options runtime --sign "$SIGNING_IDENTITY" "$app/Contents/MacOS/createdump"
  fi
  codesign --force --timestamp --options runtime --entitlements "$root/packaging/macos/entitlements.plist" --sign "$SIGNING_IDENTITY" "$app/Contents/MacOS/PhotoScreener.Desktop"
  codesign --force --timestamp --options runtime --entitlements "$root/packaging/macos/entitlements.plist" --sign "$SIGNING_IDENTITY" "$app"
  codesign --verify --deep --strict --verbose=2 "$app"
fi
ditto -c -k --keepParent "$app" "$root/artifacts/PhotoScreener-osx-arm64.zip"
if [[ "${1:-}" == '--notarize' ]]; then
  : "${NOTARY_PROFILE:?Set a configured notarytool keychain profile.}"
  : "${SIGNING_IDENTITY:?Signing is required before notarization.}"
  xcrun notarytool submit "$root/artifacts/PhotoScreener-osx-arm64.zip" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$app"
  spctl --assess --type execute --verbose=2 "$app"
  ditto -c -k --keepParent "$app" "$root/artifacts/PhotoScreener-osx-arm64.zip"
fi
echo "$app"
