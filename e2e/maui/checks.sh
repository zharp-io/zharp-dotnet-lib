#!/usr/bin/env bash
# Shared by the MAUI e2e scripts: the stand-in Zharp, what it got, and PASS/FAIL lines.
#   OUT      where the stand-in writes (bodies, artifacts/)
#   BUILD    where the build's uploads went (artifacts/ from Zharp.Build)
FAILED=0
MOCK=""
start_mock() {
  python3 e2e/mock_zharp.py "$OUT" >> "$OUT-mock.log" 2>&1 &
  MOCK=$!
  for _ in $(seq 1 20); do
    kill -0 "$MOCK" 2>/dev/null || { echo "the stand-in endpoint exited (port 4318 taken?)"; cat "$OUT-mock.log"; exit 1; }
    curl -s -o /dev/null -X POST http://127.0.0.1:4318/ping && return 0; sleep 0.3
  done
  echo "the stand-in endpoint did not start"; exit 1
}
stop_mock() { [ -n "$MOCK" ] && kill "$MOCK" 2>/dev/null; MOCK=""; sleep 1; }
clear_sent() { rm -f "$OUT"/otlp_v1_*; }
sent() { cat "$OUT"/otlp_v1_* 2>/dev/null | tr -c '[:print:]\n' '.'; }
expect() { if eval "$1"; then echo "PASS  $NAME $2"; else echo "FAIL  $NAME $2"; FAILED=1; fi; }
mvid_of_sent() { sent | grep -o '"mvid":"[0-9a-f-]\{36\}"' | head -1 | cut -d'"' -f4; }

check_error() {
  local s; s="$(sent)"
  expect 'grep -q "Mobile cart is empty" <<<"$s"' "caught error sent"
  expect 'grep -q "zharp.il_frames" <<<"$s" && grep -Eq "\"token\":[0-9]+,\"il_offset\":-?[0-9]+" <<<"$s"' "line-less frames carry token and IL offset"
  expect 'grep -q "at Shop.Mobile.Checkout.Total(Int32\[\] prices)" <<<"$s"' "the stack reads as .NET writes it"
  expect 'grep -q "session.id" <<<"$s" && grep -q "device.id" <<<"$s"' "session and device"
  expect 'grep -q "screen.name" <<<"$s" && grep -q "MainPage" <<<"$s"' "the page is the screen"
  expect 'grep -q "os.name" <<<"$s" && grep -q "$OS_NAME" <<<"$s"' "os.name $OS_NAME"
  expect 'grep -q "io.zharp.e2e.shop" <<<"$s" && grep -q "1.4.0 (212)" <<<"$s"' "service is the app ID, release 1.4.0 (212)"
  local mvid; mvid="$(mvid_of_sent)"
  echo "  frames' MVID: $mvid; uploads: $(ls "$BUILD/artifacts" | grep -v '\.json$' | tr '\n' ' ')"
  expect '[ -n "$mvid" ] && ls "$BUILD/artifacts" | grep -q "portable-pdb-$mvid-ShopApp.pdb$"' "the frames' MVID is one Zharp.Build uploaded the PDB under"
  expect '[ -n "$mvid" ] && ls "$BUILD/artifacts" | grep -q "source-bundle-$mvid-sources.json.gz$"' "the source bundle is under the same MVID"
}

check_crash() {
  local s; s="$(sent)"
  expect 'grep -q "Mobile cart is empty" <<<"$s" && grep -q "zharp.error.kind" <<<"$s" && grep -q "crash" <<<"$s"' "the crash was sent on the next launch"
}

check_hang() {
  local s; s="$(sent)"
  expect 'grep -q "ApplicationNotResponding" <<<"$s" && grep -q "The UI thread was blocked" <<<"$s"' "the hang reported"
}
