#!/usr/bin/env bash
set -euo pipefail
umask 077

app=$1
arch=$2
case "$arch" in arm64|x86_64) ;; *) exit 1 ;; esac
: "${SPARKLE_PUBLIC_KEY:?Set the SPARKLE_PUBLIC_KEY repository variable}"
: "${SPARKLE_PRIVATE_KEY:?Set the SPARKLE_PRIVATE_KEY repository secret}"
: "${MACOS_CERTIFICATE_BASE64:?Set the MACOS_CERTIFICATE_BASE64 repository secret}"
: "${MACOS_CERTIFICATE_PASSWORD:?Set the MACOS_CERTIFICATE_PASSWORD repository secret}"
: "${MACOS_NOTARY_KEY_BASE64:?Set the MACOS_NOTARY_KEY_BASE64 repository secret}"
: "${MACOS_NOTARY_KEY_ID:?Set the MACOS_NOTARY_KEY_ID repository secret}"
: "${MACOS_NOTARY_ISSUER:?Set the MACOS_NOTARY_ISSUER repository secret}"

temp=$(mktemp -d)
keychain="$temp/signing.keychain-db"
keychain_password=$(openssl rand -hex 24)
trap 'security delete-keychain "$keychain" >/dev/null 2>&1 || true; rm -rf -- "$temp"' EXIT

release_url="https://github.com/$GITHUB_REPOSITORY/releases"
appcast="appcast-macos-$arch.xml"
archive="gradient-native-macos-$arch.tar.gz"
plist="$app/Contents/Info.plist"
plutil -insert SUPublicEDKey -string "$SPARKLE_PUBLIC_KEY" "$plist"
plutil -insert SUFeedURL -string "$release_url/latest/download/$appcast" "$plist"
plutil -insert SUEnableAutomaticChecks -bool NO "$plist"
plutil -insert SUAllowsAutomaticUpdates -bool NO "$plist"
plutil -insert SUVerifyUpdateBeforeExtraction -bool YES "$plist"
plutil -insert SURequireSignedFeed -bool YES "$plist"
plutil -lint "$plist"

printf '%s' "$MACOS_CERTIFICATE_BASE64" | base64 -D > "$temp/signing.p12"
printf '%s' "$MACOS_NOTARY_KEY_BASE64" | base64 -D > "$temp/notary.p8"
security create-keychain -p "$keychain_password" "$keychain"
security unlock-keychain -p "$keychain_password" "$keychain"
security import "$temp/signing.p12" -k "$keychain" -f pkcs12 -P "$MACOS_CERTIFICATE_PASSWORD" -T /usr/bin/codesign
security set-key-partition-list -S apple-tool:,apple: -s -k "$keychain_password" "$keychain" >/dev/null
identity=$(security find-identity -v -p codesigning "$keychain" | awk '/Developer ID Application/ && !found++ { print $2 }')
test -n "$identity"
sign=(codesign --force --timestamp --options runtime --keychain "$keychain" --sign "$identity")
framework="$app/Contents/Frameworks/Sparkle.framework"
sparkle_version="$framework/Versions/Current"
for target in "$sparkle_version/Autoupdate" "$sparkle_version/Updater.app" "$sparkle_version"/XPCServices/*.xpc; do
  if [[ -e "$target" ]]; then "${sign[@]}" --preserve-metadata=entitlements "$target"; fi
done
"${sign[@]}" "$framework"
"${sign[@]}" "$app/Contents/MacOS/gradient-core"
"${sign[@]}" "$app"
codesign --verify --deep --strict --verbose=2 "$app"

ditto -c -k --sequesterRsrc --keepParent "$app" "$temp/notary.zip"
xcrun notarytool submit "$temp/notary.zip" --key "$temp/notary.p8" --key-id "$MACOS_NOTARY_KEY_ID" \
  --issuer "$MACOS_NOTARY_ISSUER" --wait --output-format json > "$temp/notary.json"
jq -e '.status == "Accepted"' "$temp/notary.json"
xcrun stapler staple "$app"

tar -czf "$archive" -C "$(dirname "$app")" "$(basename "$app")"
curl --fail --silent --show-error --location \
  https://github.com/sparkle-project/Sparkle/releases/download/2.10.1/Sparkle-2.10.1.tar.xz \
  -o "$temp/Sparkle.tar.xz"
(
  cd "$temp"
  printf '%s  %s\n' '1a856d2a9edb8e628de88121d7be04cf0e2167f8641650b78fca00b14244cf27' Sparkle.tar.xz | shasum -a 256 -c
  mkdir tools
  tar -xJf Sparkle.tar.xz -C tools
)
sign_update=$(find "$temp/tools" -type f -name sign_update -print -quit)
test -x "$sign_update"
signature=$(printf '%s\n' "$SPARKLE_PRIVATE_KEY" | "$sign_update" --ed-key-file - -p "$archive")
[[ "$signature" =~ ^[A-Za-z0-9+/]+=*$ ]]
size=$(stat -f %z "$archive")

cat > "$appcast" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>Gradient Launcher</title>
    <item>
      <title>Gradient Launcher $GRADIENT_NATIVE_VERSION</title>
      <sparkle:version>$GRADIENT_NATIVE_VERSION</sparkle:version>
      <sparkle:shortVersionString>$GRADIENT_NATIVE_VERSION</sparkle:shortVersionString>
      <sparkle:minimumSystemVersion>15.0</sparkle:minimumSystemVersion>
      <enclosure url="$release_url/download/$GITHUB_REF_NAME/$archive" sparkle:edSignature="$signature" length="$size" type="application/octet-stream"/>
    </item>
  </channel>
</rss>
EOF
printf '%s\n' "$SPARKLE_PRIVATE_KEY" | "$sign_update" --ed-key-file - "$appcast" >/dev/null
