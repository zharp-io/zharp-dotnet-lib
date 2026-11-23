#!/usr/bin/env bash
# Zharp.Godot in a Godot 4.5 .NET game, headless in Docker, against e2e/mock_zharp.py:
#   1. pack Zharp and Zharp.Godot (SDK 10):
#      docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
#        dotnet pack src/Zharp.Godot/Zharp.Godot.csproj -c Release -o artifacts/godot-e2e
#   2. run the game (the zharp-godot-sdk repo's e2e image, with the addon from that repo):
#      docker run --rm -v "$PWD:/src:ro" -v "<zharp-godot-sdk>:/godot:ro" zharp-godot-test:4.5.1 bash /src/e2e/godot/run.sh
set -u
W=/w; OUT=$W/out
rm -rf $W && mkdir -p $W $OUT /packages && cp /src/artifacts/godot-e2e/*.nupkg /packages/
cp -r /src/e2e/godot/ShopQuest $W/game && cp -r /godot/addons $W/game/addons
python3 /src/e2e/mock_zharp.py $OUT > $W/mock.log 2>&1 &
cd $W/game
timeout 120 godot-mono --headless --import > $W/import.log 2>&1
dotnet build ShopQuest.csproj > $W/build.log 2>&1 || { echo "build failed"; tail -30 $W/build.log; exit 1; }
FAILED=0
sent() { cat $OUT/v1_* 2>/dev/null | LC_ALL=C tr -c '[:print:]\n' '.'; }
expect() { if eval "$1"; then echo "PASS  $NAME $2"; else echo "FAIL  $NAME $2"; FAILED=1; fi; }
run() { ZHARP_DEBUG=1 timeout 60 godot-mono --headless -- --mode=$1 > $W/$2.log 2>&1; echo "  ran $2: exit $?"; }
# What the .NET SDK sent (OTLP protobuf); the addon sends JSON.
dotnet_sent() { for f in $OUT/v1_*; do grep -q '"resource\(Logs\|Spans\)"' "$f" || cat "$f"; done 2>/dev/null | LC_ALL=C tr -c '[:print:]\n' '.'; }

NAME=error
run error error-1
s="$(sent)"
expect 'grep -q "Cart is empty" <<<"$(dotnet_sent)" && grep -q "manual" <<<"$(dotnet_sent)"' "a caught C# exception, through the .NET SDK"
expect 'grep -q "at ShopQuest.Cart.Checkout()" <<<"$s"' "with its .NET frames"
expect 'grep -q "\"exception.type\",\"value\":{\"stringValue\":\"System.InvalidOperationException\"" <<<"$s" && grep -q "Card declined" <<<"$s"' "an uncaught one in _Ready, through the addon's Logger"
expect 'grep -q "localhost:4318/scores.json" <<<"$(dotnet_sent)" && grep -q "http.response.status_code" <<<"$(dotnet_sent)"' "HttpClient traced (ZharpGodot.HttpHandler)"
gd_session=$(sent | grep -o '"session.id","value":{"stringValue":"[0-9a-f]\{32\}"' | head -1 | grep -o '[0-9a-f]\{32\}')
gd_device=$(sent | grep -o '"device.id","value":{"stringValue":"[0-9a-f]\{32\}"' | head -1 | grep -o '[0-9a-f]\{32\}')
echo "  addon session $gd_session, device $gd_device"
dn="$(dotnet_sent)"
expect '[ -n "$gd_session" ] && grep -q "$gd_session" <<<"$dn"' "the .NET SDK's records carry the addon's session"
expect '[ -n "$gd_device" ] && grep -q "$gd_device" <<<"$dn"' "and its device"
expect 'grep -q "screen.name" <<<"$dn" && grep -q "main" <<<"$dn" && grep -q "player-7" <<<"$dn"' "the scene and the player"
expect 'grep -q "shop-quest-cs" <<<"$dn" && grep -q "shop-quest-cs" <<<"$s"' "both report the game as service shop-quest-cs"
mkdir -p $W/error && mv $OUT/v1_* $W/error/ 2>/dev/null

NAME=crash
run crash crash-1
run after after-1
s="$(sent)"
expect 'grep -q "Inventory corrupted on a worker thread" <<<"$s" && grep -q "zharp.error.kind" <<<"$s"' "a C# exception on a worker thread: the crash, sent at once or on the next launch"
expect 'grep -q "$gd_device" <<<"$(dotnet_sent)"' "on the addon's device"
[ -d /results ] && cp -r $W/*.log $W/error $OUT /results/ 2>/dev/null
exit $FAILED
