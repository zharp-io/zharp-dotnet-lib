#!/usr/bin/env bash
# Run the dotnet CLI in Docker with this repository mounted at /repo (no .NET on the host).
#   e2e/dotnet.sh [8.0|10.0|11.0] <dotnet args…>      (default image: 10.0)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TAG=10.0
case "${1:-}" in 8.0|9.0|10.0|11.0) TAG="$1"; shift;; esac
export MSYS_NO_PATHCONV=1
exec docker run --rm -v "$ROOT":/repo -v zharp-nuget:/root/.nuget/packages -w "${WORKDIR:-/repo}" \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  mcr.microsoft.com/dotnet/sdk:$TAG dotnet "$@"
