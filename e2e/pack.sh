#!/usr/bin/env bash
# Build the Zharp packages into artifacts/packages (what the e2e apps reference), and drop any
# copies of them from the shared NuGet cache so the apps get these, not an older build.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export MSYS_NO_PATHCONV=1
rm -rf "$ROOT/artifacts/packages"
docker run --rm -v "$ROOT":/repo -v zharp-nuget:/root/.nuget/packages -w /repo -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'rm -rf /root/.nuget/packages/zharp* && dotnet pack Zharp.sln -c Release -o artifacts/packages -v q -nologo 2>&1 | grep -E "error|warning (CS|NU)" | sort -u ; ls artifacts/packages'
