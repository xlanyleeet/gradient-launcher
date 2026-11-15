#!/usr/bin/env bash
set -euo pipefail

app=$1
arch=$2
case "$arch" in
  arm64) platform="Apple Silicon" ;;
  x86_64) platform="Intel" ;;
  *) echo "Unsupported macOS architecture: $arch" >&2; exit 1 ;;
esac

temp=$(mktemp -d)
mount="$temp/mount"
mounted=false
cleanup() {
  if "$mounted"; then hdiutil detach "$mount" -quiet || return; fi
  rm -rf -- "$temp"
}
trap cleanup EXIT

bundle=$(basename "$app")
image="gradient-native-macos-$arch.dmg"
mkdir -p "$temp/image" "$mount"
ditto "$app" "$temp/image/$bundle"
ln -s /Applications "$temp/image/Applications"
hdiutil create -srcfolder "$temp/image" -volname "Gradient Launcher ($platform)" \
  -fs HFS+ -format UDZO -ov "$image"

hdiutil verify "$image"
hdiutil attach "$image" -readonly -nobrowse -mountpoint "$mount" -quiet
mounted=true
test "$(readlink "$mount/Applications")" = /Applications
test -f "$mount/$bundle/Contents/Info.plist"
for binary in Gradient gradient-core; do
  test "$(lipo -archs "$mount/$bundle/Contents/MacOS/$binary")" = "$arch"
done
hdiutil detach "$mount" -quiet
mounted=false
