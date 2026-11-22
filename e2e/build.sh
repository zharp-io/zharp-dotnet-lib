#!/usr/bin/env bash
# Publish an e2e app in Docker into e2e/.out/<App>-<tfm>/. The build runs from a copy in the
# container, so the published PDBs name paths that don't exist where the app runs: the code shown
# with its errors comes from the sources embedded in the PDB (or a source bundle), never the disk.
#   e2e/build.sh <App> [net8.0|net10.0|net11.0] [extra dotnet publish args…]
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$1"; TFM="${2:-net10.0}"; shift; [ $# -gt 0 ] && shift
TAG="${TFM#net}"
OUT="${OUT_NAME:-$APP-$TFM}"
export MSYS_NO_PATHCONV=1
mkdir -p "$ROOT/e2e/.out"
rm -rf "$ROOT/e2e/.out/$OUT"
docker run --rm -v "$ROOT":/repo -v zharp-nuget:/root/.nuget/packages -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  ${BUILD_ENV:-} mcr.microsoft.com/dotnet/sdk:$TAG bash -c "
    mkdir -p /build/e2e && cp -r /repo/e2e/apps /build/e2e/apps && cp -r /repo/artifacts /build/artifacts &&
    if [ -n \"${SHIFT_LINES:-}\" ]; then for i in \$(seq 1 ${SHIFT_LINES:-0}); do sed -i '1i // (shifted)' /build/e2e/apps/$APP/Program.cs; done; fi;
    cd /build/e2e/apps/$APP && dotnet publish -c Release -p:AppTfm=$TFM -o /repo/e2e/.out/$OUT -nologo -v q $* > /tmp/build.log 2>&1; status=\$?;
    grep -E 'Zharp:|error|warning' /tmp/build.log | grep -v NU1603 | sort -u | tail -40; exit \$status"
ls "$ROOT/e2e/.out/$OUT" | grep -E "^$APP\.(dll|pdb)$"
