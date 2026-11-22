#!/usr/bin/env bash
# MAUI on the Android emulator: run by the android-emulator-runner action (ubuntu, KVM).
set -uo pipefail
NAME=android OS_NAME=Android OUT="$RUNNER_TEMP/android" BUILD="$RUNNER_TEMP/build"
source e2e/maui/checks.sh
mkdir -p "$OUT"
APP=io.zharp.e2e.shop
APK="$(ls e2e/maui/ShopApp/bin/Release/net10.0-android/*/*-Signed.apk e2e/maui/ShopApp/bin/Release/net10.0-android/*-Signed.apk 2>/dev/null | head -1)"
echo "APK: $APK"
adb install -r "$APK" || exit 1

run() { # mode seconds
  adb shell am force-stop "$APP"
  adb logcat -c
  adb shell am start -n "$APP/$APP.MainActivity" -e mode "$1"
  sleep "$2"
  echo "  --- logcat ($1)"
  adb logcat -d | grep -E "Zharp|\[e2e\]|AndroidRuntime|FATAL|Mobile cart" | tail -30 | sed 's/^/    /'
  adb shell am force-stop "$APP"
}

start_mock
clear_sent; run error 15; check_error
cp -r "$OUT" "$OUT-error-snapshot"

clear_sent; stop_mock
run crash 15
echo "  outbox after the crash: $(adb shell run-as $APP ls files/zharp/outbox 2>&1 | tr '\n' ' ')"
start_mock; run quit 20; check_crash
cp -r "$OUT" "$OUT-crash-snapshot"

clear_sent; run hang 20; check_hang
stop_mock
exit $FAILED
