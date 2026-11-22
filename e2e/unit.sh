#!/usr/bin/env bash
# Unit tests on .NET 8, 10 and 11, in the .NET 11 SDK image with the 8 and 10 runtimes added.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export MSYS_NO_PATHCONV=1
docker run --rm -v "$ROOT":/repo -v zharp-nuget:/root/.nuget/packages -v zharp-dotnet-runtimes:/runtimes -w /repo \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 mcr.microsoft.com/dotnet/sdk:11.0 bash -c '
    set -e
    if ! ls /runtimes/shared/Microsoft.AspNetCore.App 2>/dev/null | grep -q "^8\."; then
      curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/i.sh
      bash /tmp/i.sh --runtime dotnet --channel 8.0 --install-dir /runtimes --no-path >/dev/null
      bash /tmp/i.sh --runtime dotnet --channel 10.0 --install-dir /runtimes --no-path >/dev/null
      bash /tmp/i.sh --runtime aspnetcore --channel 8.0 --install-dir /runtimes --no-path >/dev/null
      bash /tmp/i.sh --runtime aspnetcore --channel 10.0 --install-dir /runtimes --no-path >/dev/null
    fi
    cp -r --update=none /runtimes/shared/Microsoft.NETCore.App/* /usr/share/dotnet/shared/Microsoft.NETCore.App/
    cp -r --update=none /runtimes/shared/Microsoft.AspNetCore.App/* /usr/share/dotnet/shared/Microsoft.AspNetCore.App/
    for tfm in net8.0 net10.0 net11.0; do
      echo "== $tfm"
      dotnet test test/Zharp.Tests/Zharp.Tests.csproj -f $tfm -nologo -v q 2>&1 | grep -E "error|Failed |Passed!|Failed!|Assert|Expected|Actual" | head -40
    done'
