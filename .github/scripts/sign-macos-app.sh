#!/usr/bin/env bash
set -euo pipefail

app=$1
shift
sign=(codesign --force "$@")
framework="$app/Contents/Frameworks/Sparkle.framework"
sparkle_version="$framework/Versions/Current"
for target in "$sparkle_version/Autoupdate" "$sparkle_version/Updater.app" "$sparkle_version"/XPCServices/*.xpc; do
  if [[ -e "$target" ]]; then "${sign[@]}" --preserve-metadata=entitlements "$target"; fi
done
"${sign[@]}" "$framework"
"${sign[@]}" "$app/Contents/MacOS/gradient-core"
"${sign[@]}" "$app"
codesign --verify --deep --strict --verbose=2 "$app"
