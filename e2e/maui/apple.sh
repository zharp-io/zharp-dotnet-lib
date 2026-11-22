#!/usr/bin/env bash
# MAUI on the iOS simulator and as a Mac Catalyst app (macOS runner).
#   apple.sh ios | maccatalyst
set -uo pipefail
PLATFORM="$1"
BUILD="$RUNNER_TEMP/build-$PLATFORM" OUT="$RUNNER_TEMP/$PLATFORM" NAME="$PLATFORM"
source e2e/maui/checks.sh
mkdir -p "$OUT"
APP=io.zharp.e2e.shop

if [ "$PLATFORM" = ios ]; then
  OS_NAME=iOS
  BUNDLE="$(ls -d e2e/maui/ShopApp/bin/Release/net10.0-ios/*/ShopApp.app | head -1)"
  DEVICE="$(xcrun simctl list devices available -j | python3 -c 'import json,sys; d=json.load(sys.stdin)["devices"]; print(next(x["udid"] for k,v in sorted(d.items(), reverse=True) if "iOS" in k for x in v if x["name"].startswith("iPhone")))')"
  echo "simulator $DEVICE, app $BUNDLE"
  xcrun simctl boot "$DEVICE" 2>/dev/null; xcrun simctl bootstatus "$DEVICE" -b
  xcrun simctl install "$DEVICE" "$BUNDLE"
  run() { # mode seconds
    xcrun simctl terminate "$DEVICE" "$APP" 2>/dev/null
    xcrun simctl launch "$DEVICE" "$APP" "$1"
    sleep "$2"
    xcrun simctl terminate "$DEVICE" "$APP" 2>/dev/null
  }
  outbox() { find "$(xcrun simctl get_app_container "$DEVICE" "$APP" data)" -path '*zharp/outbox*' -type f 2>/dev/null | tr '\n' ' '; }
else
  OS_NAME=macOS
  BUNDLE="$(ls -d e2e/maui/ShopApp/bin/Release/net10.0-maccatalyst/*/ShopApp.app | head -1)"
  echo "app $BUNDLE"
  run() { # mode seconds
    "$BUNDLE/Contents/MacOS/ShopApp" "$1" > "$OUT-$1.log" 2>&1 &
    local pid=$!
    sleep "$2"
    kill "$pid" 2>/dev/null
    tail -20 "$OUT-$1.log" | sed 's/^/    /'
  }
  outbox() { find "$HOME/Library" -path '*zharp/outbox*' -type f 2>/dev/null | tr '\n' ' '; }
fi

start_mock
clear_sent; run error 15; check_error
cp -r "$OUT" "$OUT-error-snapshot"

clear_sent; stop_mock
run crash 15
echo "  outbox after the crash: $(outbox)"
start_mock; run quit 20; check_crash
cp -r "$OUT" "$OUT-crash-snapshot"

clear_sent; run hang 20; check_hang
stop_mock
exit $FAILED
