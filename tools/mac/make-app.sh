#!/usr/bin/env bash
# Usage: make-app.sh <publishDir> <outDir> <version>
# Wraps a `dotnet publish` output (osx-arm64) into "<outDir>/PS Remote Sub.app" and ad-hoc signs it.
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <publishDir> <outDir> <version>" >&2
  exit 2
fi

publish_dir=$1
out_dir=$2
version=$3
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
app="$out_dir/PS Remote Sub.app"

[ -x "$publish_dir/PSRemoteSub.App" ] || { echo "missing apphost: $publish_dir/PSRemoteSub.App" >&2; exit 1; }

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish_dir/." "$app/Contents/MacOS/"
cp "$repo/src/PSRemoteSub.App/Assets/app.icns" "$app/Contents/Resources/app.icns"
esc_version=$(printf '%s' "$version" | sed 's/[\/&]/\\&/g')
sed "s/__VERSION__/$esc_version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist" >/dev/null

# Ad-hoc signature (no Developer ID / notarization); no entitlements.
codesign --force --deep -s - "$app"
echo "$app"
