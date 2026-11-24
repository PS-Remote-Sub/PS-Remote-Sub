#!/usr/bin/env bash
# Publishes a Debug build, wraps it in a .app (so TCC permissions and Info.plist keys apply) and runs it.
set -euo pipefail

repo=$(cd "$(dirname "$0")/../.." && pwd)
cd "$repo"

dotnet publish src/PSRemoteSub.App -f net10.0 -r osx-arm64 -c Debug -o artifacts/mac-dev/publish
tools/mac/make-app.sh artifacts/mac-dev/publish artifacts/mac-dev 0.0.0-dev
open -W "artifacts/mac-dev/PS Remote Sub.app"
